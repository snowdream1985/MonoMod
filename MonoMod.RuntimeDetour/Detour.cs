﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Linq.Expressions;
using MonoMod.Utils;

namespace MonoMod.RuntimeDetour {
    /// <summary>
    /// A fully managed detour.
    /// Multiple Detours for a method to detour from can exist at any given time. Detours can be layered.
    /// If you're writing your own detour manager or need to detour native functions, it's better to create instances of NativeDetour instead.
    /// </summary>
    public class Detour : IDetour {

        private static Dictionary<MethodBase, List<Detour>> _DetourMap = new Dictionary<MethodBase, List<Detour>>();
        private static Dictionary<MethodBase, DynamicMethod> _BackupMethods = new Dictionary<MethodBase, DynamicMethod>();

        public static Func<object, MethodBase, MethodBase, bool> OnDetour;
        public static Func<object, bool> OnUndo;
        public static Func<object, MethodBase, MethodBase> OnGenerateTrampoline;

        public bool IsValid => _DetourMap[Method].Contains(this);

        public int Index {
            get {
                return _DetourMap[Method].IndexOf(this);
            }
            set {
                List<Detour> detours = _DetourMap[Method];
                lock (detours) {
                    int valueOld = detours.IndexOf(this);
                    if (valueOld == -1)
                        throw new InvalidOperationException("This detour has been undone.");

                    detours.RemoveAt(valueOld);

                    if (value > valueOld)
                        value--;

                    try {
                        detours.Insert(value, this);
                    } catch {
                        // Too lazy to manually check the bounds.
                        detours.Insert(valueOld, this);
                        throw;
                    }

                    Detour top = detours[detours.Count - 1];
                    if (top != this)
                        _TopUndo();
                    top._TopApply();
                    _UpdateChainedTrampolines(Method);
                }
            }
        }

        public readonly MethodBase Method;
        public readonly MethodBase Target;

        // The active NativeDetour. Only present if the current Detour is on the top of the Detour chain.
        private NativeDetour _TopDetour;

        // Called by the generated trampolines, updated when the Detour chain changes.
        private DynamicMethod _ChainedTrampoline;

        public Detour(MethodBase from, MethodBase to) {
            Method = from;
            Target = to;

            if (!(OnDetour?.InvokeWhileTrue(this, from, to) ?? true))
                return;

            // TODO: Check target method arguments.

            lock (_BackupMethods) {
                if (!_BackupMethods.ContainsKey(Method))
                    _BackupMethods[Method] = Method.CreateILCopy();
            }

            // Generate a "chained trampoline" DynamicMethod.
            ParameterInfo[] args = Method.GetParameters();
            Type[] argTypes;
            if (!Method.IsStatic) {
                argTypes = new Type[args.Length + 1];
                argTypes[0] = Method.DeclaringType;
                for (int i = 0; i < args.Length; i++)
                    argTypes[i + 1] = args[i].ParameterType;
            } else {
                argTypes = new Type[args.Length];
                for (int i = 0; i < args.Length; i++)
                    argTypes[i] = args[i].ParameterType;
            }

            _ChainedTrampoline = new DynamicMethod(
                $"chain_{Method.Name}_{GetHashCode()}",
                (Method as MethodInfo)?.ReturnType ?? typeof(void), argTypes,
                Method.DeclaringType,
                false // Otherwise just ret is invalid for whatever reason.
            ).StubCriticalDetour().Pin();

            // Add the detour to the detour map.
            List<Detour> detours;
            lock (_DetourMap) {
                if (!_DetourMap.TryGetValue(Method, out detours))
                    _DetourMap[Method] = detours = new List<Detour>();
            }
            lock (detours) {
                // New Detour instances are always on the top.
                if (detours.Count > 0)
                    detours[detours.Count - 1]._TopUndo();
                _TopApply();

                // Do the initial "chained trampoline" setup.
                NativeDetourData link;
                if (detours.Count > 0) {
                    // If a previous Detour exists in the chain, detour our "chained trampoline" to it,
                    link = DetourHelper.Native.Create(
                        _ChainedTrampoline.GetNativeStart(),
                        detours[detours.Count - 1].Target.GetNativeStart()
                    );
                } else {
                    // If this is the first Detour in the chain, detour our "chained trampoline" to the original method.
                    link = DetourHelper.Native.Create(
                        _ChainedTrampoline.GetNativeStart(),
                        _BackupMethods[Method].GetNativeStart()
                    );
                }
                DetourHelper.Native.MakeWritable(link);
                DetourHelper.Native.Apply(link);
                DetourHelper.Native.MakeExecutable(link);
                DetourHelper.Native.Free(link);

                detours.Add(this);
            }
        }

        public Detour(MethodBase method, IntPtr to)
            : this(method, DetourHelper.GenerateNativeProxy(to, method)) {
        }

        public Detour(Delegate from, IntPtr to)
            : this(from.GetMethodInfo(), to) {
        }
        public Detour(Delegate from, Delegate to)
            : this(from.GetMethodInfo(), to.GetMethodInfo()) {
        }

        public Detour(Expression from, IntPtr to)
            : this(((MethodCallExpression) from).Method, to) {
        }
        public Detour(Expression from, Expression to)
            : this(((MethodCallExpression) from).Method, ((MethodCallExpression) to).Method) {
        }

        public Detour(Expression<Action> from, IntPtr to)
            : this(from.Body, to) {
        }
        public Detour(Expression<Action> from, Expression<Action> to)
            : this(from.Body, to.Body) {
        }

        /// <summary>
        /// This is a no-op on fully managed detours.
        /// </summary>
        public void Apply() {
            if (!IsValid)
                throw new InvalidOperationException("This detour has been undone.");

            // no-op.
        }

        /// <summary>
        /// Permanently undo the detour, while also freeing any related unmanaged resources. This makes any further operations on this detour invalid.
        /// </summary>
        public void Undo() {
            if (!(OnUndo?.InvokeWhileTrue(this) ?? true))
                return;

            if (!IsValid)
                return;

            List<Detour> detours = _DetourMap[Method];
            lock (detours) {
                detours.Remove(this);
                _TopUndo();
                if (detours.Count > 0)
                    detours[detours.Count - 1]._TopApply();
                _UpdateChainedTrampolines(Method);
            }
        }

        /// <summary>
        /// Free the detour, while also permanently undoing it. This makes any further operations on this detour invalid.
        /// </summary>
        public void Free() {
            // NativeDetour allows freeing without undoing, but Detours are fully managed.
            // Freeing a Detour without undoing it would leave a hole open in the detour chain.
            if (!IsValid)
                return;
            Undo();
        }

        /// <summary>
        /// Undo and free this temporary detour.
        /// </summary>
        public void Dispose() {
            Undo();
            Free();
        }

        /// <summary>
        /// Generate a new DynamicMethod with which you can invoke the previous state.
        /// </summary>
        public MethodBase GenerateTrampoline(MethodBase signature = null) {
            MethodBase remoteTrampoline = OnGenerateTrampoline?.InvokeWhileNull<MethodBase>(this, signature);
            if (remoteTrampoline != null)
                return remoteTrampoline;

            if (signature == null)
                signature = Target;

            // Note: It'd be more performant to skip this step and just return the "chained trampoline."
            // Unfortunately, it'd allow a third party to break the Detour trampoline chain, among other things.
            // Instead, we create and return a DynamicMethod calling the "chained trampoline."

            Type returnType = (signature as MethodInfo)?.ReturnType ?? typeof(void);

            ParameterInfo[] args = signature.GetParameters();
            Type[] argTypes = new Type[args.Length];
            for (int i = 0; i < args.Length; i++)
                argTypes[i] = args[i].ParameterType;

            DynamicMethod dm;
            ILGenerator il;

            dm = new DynamicMethod(
                $"trampoline_{Method.Name}_{GetHashCode()}",
                returnType, argTypes,
                Method.DeclaringType,
                true
            );

            il = dm.GetILGenerator();

            for (int i = 0; i < 10; i++) {
                // Prevent old Unity mono from inlining the DynamicMethod.
                il.Emit(OpCodes.Nop);
            }
            il.Emit(OpCodes.Jmp, _ChainedTrampoline);

            return dm.Pin();
        }

        /// <summary>
        /// Generate a new DynamicMethod with which you can invoke the previous state.
        /// </summary>
        public T GenerateTrampoline<T>() where T : Delegate {
            if (!typeof(Delegate).GetTypeInfo().IsAssignableFrom(typeof(T)))
                throw new InvalidOperationException($"Type {typeof(T)} not a delegate type.");

            return GenerateTrampoline(typeof(T).GetTypeInfo().GetMethod("Invoke")).CreateDelegate(typeof(T)) as T;
        }

        private void _TopUndo() {
            if (_TopDetour == null)
                return;

            _TopDetour.Undo();
            _TopDetour.Free();
            _TopDetour = null;
        }
        private void _TopApply() {
            if (_TopDetour != null)
                return;

            // GetNativeStart to prevent managed backups.
            _TopDetour = new NativeDetour(Method.GetNativeStart(), Target.GetNativeStart());
        }

        private static void _UpdateChainedTrampolines(MethodBase method) {
            List<Detour> detours = _DetourMap[method];
            lock (detours) {
                if (detours.Count == 0)
                    return;

                NativeDetourData link;

                for (int i = 1; i < detours.Count; i++) {
                    link = DetourHelper.Native.Create(
                        detours[i]._ChainedTrampoline.GetNativeStart(),
                        detours[i - 1].Target.GetNativeStart()
                    );
                    DetourHelper.Native.MakeWritable(link);
                    DetourHelper.Native.Apply(link);
                    DetourHelper.Native.MakeExecutable(link);
                    DetourHelper.Native.Free(link);
                }

                link = DetourHelper.Native.Create(
                    detours[0]._ChainedTrampoline.GetNativeStart(),
                    _BackupMethods[method].GetNativeStart()
                );
                DetourHelper.Native.MakeWritable(link);
                DetourHelper.Native.Apply(link);
                DetourHelper.Native.MakeExecutable(link);
                DetourHelper.Native.Free(link);
            }
        }
    }

    public class Detour<T> : Detour where T : Delegate {
        public Detour(T from, IntPtr to)
            : base(from, to) {
        }
        public Detour(T from, T to)
            : base(from, to) {
        }
    }
}
