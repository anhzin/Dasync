﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using Dasync.ValueContainer;

namespace Dasync.Proxy
{
    public class ProxyTypeBuilder : IProxyTypeBuilder
    {
        private static readonly Lazy<ModuleBuilder> _moduleBuilder;

        private static readonly Dictionary<Type[], Type> _proxyTypeMap
            = new Dictionary<Type[], Type>(TypeSetComparer.Instance);

        private sealed class TypeSetComparer : IEqualityComparer<Type[]>
        {
            public static readonly TypeSetComparer Instance = new TypeSetComparer();

            public bool Equals(Type[] x, Type[] y)
            {
#warning Need to normalize/denormalize first (interfaces can implement interfaces)
                if (x.Length != y.Length)
                    return false;
                Array.Sort(x, (a, b) => string.CompareOrdinal(a.AssemblyQualifiedName, b.AssemblyQualifiedName));
                Array.Sort(y, (a, b) => string.CompareOrdinal(a.AssemblyQualifiedName, b.AssemblyQualifiedName));
                for (var i = 0; i < x.Length; i++)
                    if (x[i] != y[i])
                        return false;
                return true;
            }

            public int GetHashCode(Type[] typeArray)
            {
#warning Need to normalize/denormalize first (interfaces can implement interfaces)
                Array.Sort(typeArray, (a, b) => string.CompareOrdinal(a.AssemblyQualifiedName, b.AssemblyQualifiedName));
                int code = 0;
                unchecked
                {
                    foreach (var type in typeArray)
                        code = (code * 1137) ^ type.GetHashCode();
                }
                return code;
            }
        }

        static ProxyTypeBuilder()
        {
            _moduleBuilder = new Lazy<ModuleBuilder>(() =>
            {
                var assemblyName = new AssemblyName(typeof(ProxyTypeBuilder)
                    .GetTypeInfo().Assembly.GetName().Name);
                var assemblyBuilder =
#if NETFX
                    AppDomain.CurrentDomain.
#else
                    AssemblyBuilder.
#endif
                    DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);

                var moduleName = assemblyName.Name + ".dll";
                return assemblyBuilder.DefineDynamicModule(moduleName);
            });
        }

        public Type Build(Type baseClass)
        {
            lock (_proxyTypeMap)
            {
                var key = new[] { baseClass };
                if (!_proxyTypeMap.TryGetValue(key, out var result))
                {
                    result = BuildType(key);
                    _proxyTypeMap.Add(key, result);
                }
                return result;
            }
        }

        public Type Build(IEnumerable<Type> interfacesTypes)
        {
            lock (_proxyTypeMap)
            {
                var key = interfacesTypes.ToArray();
                if (!_proxyTypeMap.TryGetValue(key, out var result))
                {
                    result = BuildType(key);
                    _proxyTypeMap.Add(key, result);
                }
                return result;
            }
        }

        private static int _counter;

        public Type BuildType(Type[] interfacesTypes)
        {
            var isClass = interfacesTypes.Length == 1 && interfacesTypes[0].GetTypeInfo().IsClass;
            var objectType = isClass ? interfacesTypes[0] : null;

            //if (!objectType.IsInterface)
            //    throw new ArgumentException($"Cannot generate proxy for the type '{objectType.FullName}' because it's not an interface", nameof(objectType));
            if (isClass && !objectType.GetTypeInfo().IsPublic)
                throw new ArgumentException($"Cannot generate proxy for the interface '{objectType.FullName}' because it's not public", nameof(objectType));
            if (isClass && objectType.GetTypeInfo().IsSealed)
                throw new ArgumentException($"The type '{objectType.FullName}' must not be sealed", nameof(objectType));

            string proxyTypeFullName;

            if (isClass)
            {
                proxyTypeFullName = objectType.FullName + "!proxy";
            }
            else
            {
                var id = Interlocked.Increment(ref _counter);
                proxyTypeFullName = $"!proxy{id}";
            }

            var typeBuilder = _moduleBuilder.Value.DefineType(proxyTypeFullName,
                TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.Sealed,
                isClass ? objectType : typeof(object));

            var context = new Context
            {
                TypeBuilder = typeBuilder,
                ObjectType = objectType
            };

            ImplementObjectProxy(context);
            //AddConstructor(context);
            //AddFactoryMethod(context);

            if (isClass)
            {
                CopyConstructors(context);
                OverrideClassMethods(context);
            }
            else
            {
                foreach (var interfaceType in interfacesTypes)
                    ImplementInterfaceMethods(interfaceType, context);
            }

            return typeBuilder
#if NETFX
                .CreateType();
#else
                .CreateTypeInfo().AsType();
#endif
        }

        private static void ImplementObjectProxy(Context context)
        {
            context.TypeBuilder.AddInterfaceImplementation(typeof(IProxy));

            // -------------------------------------------

            var objectTypeProperty = context.TypeBuilder.DefineProperty(
                nameof(IProxy.ObjectType), PropertyAttributes.None, typeof(Type), null);

            var getObjectTypeMethod = context.TypeBuilder.DefineMethod(
                "get_" + nameof(IProxy.ObjectType),
                MethodAttributes.SpecialName | MethodAttributes.Public | MethodAttributes.Virtual,
                typeof(Type), null);
            {
                var il = getObjectTypeMethod.GetILGenerator();
                if (context.ObjectType == null)
                {
                    il.Emit(OpCodes.Ldnull);
                }
                else
                {
                    il.Emit(OpCodes.Ldtoken, context.ObjectType);
                    il.Emit(OpCodes.Call, GetTypeFromHandleMethod);
                }
                il.Emit(OpCodes.Ret);
            }

            objectTypeProperty.SetGetMethod(getObjectTypeMethod);

            context.TypeBuilder.DefineMethodOverride(getObjectTypeMethod,
                typeof(IProxy).GetProperty(nameof(IProxy.ObjectType)).GetMethod);

            // -------------------------------------------

            var executorField = context.TypeBuilder.DefineField(
                "_executor", typeof(IProxyMethodExecutor), FieldAttributes.Private);
            var executorProperty = context.TypeBuilder.DefineProperty(
                nameof(IProxy.Executor), PropertyAttributes.None, typeof(IProxyMethodExecutor), null);

            var getExecutorMethod = context.TypeBuilder.DefineMethod(
                "get_" + nameof(IProxy.Executor),
                MethodAttributes.SpecialName | MethodAttributes.Public | MethodAttributes.Virtual,
                typeof(IProxyMethodExecutor), null);
            {
                var il = getExecutorMethod.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, executorField);
                il.Emit(OpCodes.Ret);
            }

            executorProperty.SetGetMethod(getExecutorMethod);

            context.TypeBuilder.DefineMethodOverride(getExecutorMethod,
                typeof(IProxy).GetProperty(nameof(IProxy.Executor)).GetMethod);

            var setExecutorMethod = context.TypeBuilder.DefineMethod(
                "set_" + nameof(IProxy.Executor),
                MethodAttributes.SpecialName | MethodAttributes.Public | MethodAttributes.Virtual,
                typeof(void), new Type[] { typeof(IProxyMethodExecutor) });
            {
                var il = setExecutorMethod.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Stfld, executorField);
                il.Emit(OpCodes.Ret);
            }

            executorProperty.SetSetMethod(setExecutorMethod);

            context.TypeBuilder.DefineMethodOverride(setExecutorMethod,
                typeof(IProxy).GetProperty(nameof(IProxy.Executor)).SetMethod);


            // -------------------------------------------


            var contextField = context.TypeBuilder.DefineField(
                "_context", typeof(object), FieldAttributes.Private);
            var contextProperty = context.TypeBuilder.DefineProperty(
                nameof(IProxy.Context), PropertyAttributes.None, typeof(object), null);


            var getContextMethod = context.TypeBuilder.DefineMethod(
                "get_" + nameof(IProxy.Context),
                MethodAttributes.SpecialName | MethodAttributes.Public | MethodAttributes.Virtual,
                typeof(object), null);
            {
                var il = getContextMethod.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, contextField);
                il.Emit(OpCodes.Ret);
            }

            contextProperty.SetGetMethod(getContextMethod);

            context.TypeBuilder.DefineMethodOverride(getContextMethod,
                typeof(IProxy).GetProperty(nameof(IProxy.Context)).GetMethod);

            var setContextMethod = context.TypeBuilder.DefineMethod(
                "set_" + nameof(IProxy.Context),
                MethodAttributes.SpecialName | MethodAttributes.Public | MethodAttributes.Virtual,
                typeof(void), new[] { typeof(object) });
            {
                var il = setContextMethod.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Stfld, contextField);
                il.Emit(OpCodes.Ret);
            }

            contextProperty.SetSetMethod(setContextMethod);

            context.TypeBuilder.DefineMethodOverride(setContextMethod,
                typeof(IProxy).GetProperty(nameof(IProxy.Context)).SetMethod);


            // -------------------------------------------

            context.ExecutorField = executorField;
        }

        /*private static void AddConstructor(Context context)
        {
            // Add constructor to the stub type:
            //
            // public class TWorkflowStub : WorkflowStub, TWorkflow
            // {
            //   public TWorkflowStub(IMethodStubExecutor methodStubExecutor)
            //     : base(typeof(TWorkflow), methodStubExecutor) { }
            // }

            var baseCtor = context.StubTypeBuilder.BaseType
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                .SingleOrDefault(ctor => ctor.GetParameters().Length == 2 &&
                    ctor.GetParameters()[0].ParameterType == typeof(Type) &&
                    ctor.GetParameters()[1].ParameterType == typeof(IMethodStubExecutor));
            if (baseCtor == null)
                throw new InvalidOperationException("no base ctor found");

            var ctorBuilder = context.StubTypeBuilder.DefineConstructor(
                MethodAttributes.Public,
                CallingConventions.Standard | CallingConventions.HasThis,
                Types_WorkflowStubCtorArgs);

            var ilGenerator = ctorBuilder.GetILGenerator();
            // load 'this'
            ilGenerator.Emit(OpCodes.Ldarg_0);
            // load typeof(TWorkflow)
            ilGenerator.Emit(OpCodes.Ldtoken, context.ObjectType);
            ilGenerator.Emit(OpCodes.Call, GetTypeFromHandleMethod);
            // load methodStubExecutor
            ilGenerator.Emit(OpCodes.Ldarg_1);
            // call ': base(interfaceType, methodStubExecutor)'
            ilGenerator.Emit(OpCodes.Call, baseCtor);
            // return
            ilGenerator.Emit(OpCodes.Ret);

            context.Constructor = ctorBuilder;
        }*/

        /*private static void AddFactoryMethod(Context context)
        {
            // Add factory method:
            //
            // public class TWorkflowStub : WorkflowStub, TWorkflow
            // {
            //   public static WorkflowStub Create(IMethodStubExecutor methodStubExecutor)
            //     => new TWorkflowStub(methodStubExecutor);
            // }

            var methodBuilder = context.StubTypeBuilder.DefineMethod(
                "Create",
                MethodAttributes.Public | MethodAttributes.Static,
                returnType: typeof(IObjectStub),
                parameterTypes: Types_WorkflowStubCtorArgs);

            var ilGenerator = methodBuilder.GetILGenerator();
            // load methodStubExecutor
            ilGenerator.Emit(OpCodes.Ldarg_0);
            // new TWorkflowStub
            ilGenerator.Emit(OpCodes.Newobj, context.Constructor);
            // return
            ilGenerator.Emit(OpCodes.Ret);
        }*/

        private static void CopyConstructors(Context context)
        {
            var ctors = context.ObjectType.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var ctor in ctors)
            {
                var parameterTypes = ctor.GetParameters().Select(pi => pi.ParameterType).ToArray();
                var ctorCopy = context.TypeBuilder.DefineConstructor(
                    ctor.Attributes, ctor.CallingConvention, parameterTypes);

                var il = ctorCopy.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);

                for (var i = 1; i <= parameterTypes.Length; i++)
                    il.Emit(OpCodes.Ldarg_S, i);

                il.Emit(OpCodes.Call, ctor);
                il.Emit(OpCodes.Ret);

#warning copy attributes
                //foreach (var attr in ctor.GetCustomAttributesData())
                //{
                //    ctorCopy.SetCustomAttribute(new CustomAttributeBuilder(attr.Constructor,
                //        attr.ConstructorArguments.Select(a => a.Value), attr.NamedArguments.Select(a => a.));
                //}
            }
        }

        private static void ImplementInterfaceMethods(Type interfaceType, Context context)
        {
            if (interfaceType.GetProperties()?.Length > 0)
                throw new InvalidOperationException($"Cannot generate proxy for the interface '{context.ObjectType.FullName}' because it has properties");
            if (interfaceType.GetEvents()?.Length > 0)
                throw new InvalidOperationException($"Cannot generate proxy for the interface '{context.ObjectType.FullName}' because it has events");

            if (!context.ImplementedInterfaces.Add(interfaceType))
                return;

            context.TypeBuilder.AddInterfaceImplementation(interfaceType);

            foreach (var interfaceMethod in interfaceType.GetMethods().Where(t => t.IsPublic))
            {
                var methodBuilder = OverrideMethod(interfaceMethod, context);
                context.TypeBuilder.DefineMethodOverride(methodBuilder, interfaceMethod);
            }

            foreach (var implementedInterface in interfaceType.GetInterfaces().Where(IsPublic))
                ImplementInterfaceMethods(implementedInterface, context);
        }

        private static bool IsRoutine(MethodInfo mi)
        {
            return (mi.IsVirtual || mi.IsAbstract) && !mi.IsFinal && typeof(Task).IsAssignableFrom(mi.ReturnType);
        }

        private static void OverrideClassMethods(Context context)
        {
            var allInterfaces = GetClassPublicInterfaces(context.ObjectType);

            foreach (var interfaceType in allInterfaces)
            {
                context.TypeBuilder.AddInterfaceImplementation(interfaceType);

                foreach (var interfaceMethod in interfaceType.GetMethods())
                {
                    var methodBuilder = OverrideMethod(interfaceMethod, context, explicitInterfaceMethod: true);
                    context.TypeBuilder.DefineMethodOverride(methodBuilder, interfaceMethod);
                }
            }

            var methods = context.ObjectType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(IsRoutine);

            foreach (var routineMethod in methods)
            {
                var methodBuilder = OverrideMethod(routineMethod, context);
                context.TypeBuilder.DefineMethodOverride(methodBuilder, routineMethod);
            }
        }

        private static List<Type> GetClassPublicInterfaces(Type classType)
        {
            var result = new List<Type>();
            var topLevelInterfaces = classType.GetInterfaces().Where(IsPublic);
            result.AddRange(topLevelInterfaces);

            for (var i = 0; i < result.Count; i++)
            {
                var interfaceType = result[i];
                var subInterfaces = interfaceType.GetInterfaces().Where(IsPublic);
                foreach (var subInterface in subInterfaces)
                    if (!result.Contains(subInterface))
                        result.Add(subInterface);
            }

            return result;
        }

        private static MethodBuilder OverrideMethod(MethodInfo method, Context context,
            bool explicitInterfaceMethod = false)
        {
            var methodAttributes = MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual;
            if (explicitInterfaceMethod)
                methodAttributes |= MethodAttributes.HideBySig | MethodAttributes.NewSlot;

            var methodBuilder = context.TypeBuilder.DefineMethod(
                method.Name,
                methodAttributes,
                method.ReturnType,
                method.GetParameters().Select(pi => pi.ParameterType).ToArray());

            var returnsSimpleTask = method.ReturnType == typeof(Task);
            var returnsGenericTask =
                !returnsSimpleTask &&
                method.ReturnType.GetTypeInfo().IsGenericType &&
                method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>);

            if (!returnsSimpleTask && !returnsGenericTask
                // Dispose is the only non-async method that's allowed.
                && method.DeclaringType != typeof(IDisposable))
                throw new InvalidOperationException($"Cannot generate proxy for the interface '{method.DeclaringType.FullName}' because the method '{method.Name}' does not return Task or Task<>");

            var parameters = method.GetParameters();

            foreach (var p in parameters)
            {
                var position = p.Position + 1; // 0 is for the return parameter in the MethodBuilder
                var parameterBuilder = methodBuilder.DefineParameter(position, p.Attributes, p.Name);
                if (p.HasDefaultValue)
                    parameterBuilder.SetConstant(p.DefaultValue);
#warning TODO: copy custom attributes
                //foreach (var attr in p.GetCustomAttributes())
                //    parameterBuilder.SetCustomAttribute(attr);
            }

#warning TODO: add support for method generic parameters

            var parameterSetType = ValueContainerTypeBuilder.Build(
                parameters.Select(p => new KeyValuePair<string, Type>(p.Name, p.ParameterType)));

            var il = methodBuilder.GetILGenerator();

            var methodParamsVar = il.DeclareLocal(parameterSetType);

            // Create parameters container and push it on the stack
            if (parameterSetType.GetTypeInfo().IsClass)
            {
                il.Emit(OpCodes.Newobj, parameterSetType.GetConstructor(new Type[0]));
            }
            else
            {
                il.Emit(OpCodes.Ldloca_S, methodParamsVar.LocalIndex);
                il.Emit(OpCodes.Initobj, parameterSetType);
            }

            // Assign values of input parameters to members of the container
            for (var i = 0; i < parameters.Length; i++)
            {
                var param = parameters[i];
                var fieldInfo = parameterSetType.GetField(param.Name, BindingFlags.Public | BindingFlags.Instance);
                var propInfo = parameterSetType.GetProperty(param.Name, BindingFlags.Public | BindingFlags.Instance);

                if (parameterSetType.GetTypeInfo().IsClass)
                {
                    il.Emit(OpCodes.Dup);
                }
                else
                {
                    il.Emit(OpCodes.Ldloca_S, methodParamsVar.LocalIndex);
                }

                il.Emit(OpCodes.Ldarg, i + 1);

                if (fieldInfo != null)
                {
                    il.Emit(OpCodes.Stfld, fieldInfo);
                }
                else if (propInfo != null)
                {
                    il.Emit(OpCodes.Call, propInfo.SetMethod);
                }
                else
                {
                    throw new Exception();
                }
            }

            if (parameterSetType.GetTypeInfo().IsClass)
            {
                // Store instance of to the container in a local variable
                il.Emit(OpCodes.Stloc_S, methodParamsVar.LocalIndex);
            }

            // Push 'Executor'
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, context.ExecutorField);
            // Push 'this'
            il.Emit(OpCodes.Ldarg_0);
            // Push 'MethodInfo'
            il.Emit(OpCodes.Ldtoken, method);
            il.Emit(OpCodes.Call, GetMethodFromHandleMethod);
            // Push reference to 'parameters'
            il.Emit(OpCodes.Ldloca_S, methodParamsVar.LocalIndex);

            // Call Execute<TParameters>
            il.Emit(OpCodes.Callvirt, ExecuteProxyMethod.MakeGenericMethod(parameterSetType));

            // Cast result to proper generic Task<TUnwrappedResult> if needed
            if (returnsGenericTask)
                il.Emit(OpCodes.Castclass, method.ReturnType);

            // Ignore the result Task for the Dispose() method
            if (method.ReturnType == typeof(void))
                il.Emit(OpCodes.Pop);

            // Return the result Task
            il.Emit(OpCodes.Ret);

            return methodBuilder;
        }

        private sealed class Context
        {
            public Type ObjectType;
            public TypeBuilder TypeBuilder;
            public HashSet<Type> ImplementedInterfaces = new HashSet<Type>();
            //public ConstructorInfo Constructor;
            public FieldInfo ExecutorField;
        }

        private static readonly ConstructorInfo NotImplementedExceptionCtor =
            typeof(NotImplementedException).GetConstructor(new Type[] { typeof(string) });

        private static readonly MethodInfo GetTypeFromHandleMethod =
            typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle));

        private static readonly MethodInfo GetMethodFromHandleMethod =
            typeof(MethodBase).GetMethod(nameof(MethodBase.GetMethodFromHandle),
#if NETFX
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(RuntimeMethodHandle) }, null);
#else
                new[] { typeof(RuntimeMethodHandle) });
#endif

        //internal static readonly Type[] Types_WorkflowStubCtorArgs =
        //    new[] { typeof(IMethodStubExecutor) };

        //private static readonly FieldInfo MethodStubExecutorField =
        //    typeof(WorkflowStub).GetField(nameof(WorkflowStub.MethodStubExecutor),
        //        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        //private static readonly FieldInfo InterfaceTypeField =
        //    typeof(WorkflowStub).GetField(nameof(WorkflowStub.InterfaceType),
        //        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly MethodInfo ExecuteProxyMethod =
            typeof(IProxyMethodExecutor).GetMethod(nameof(IProxyMethodExecutor.Execute));

        private static bool IsPublic(Type t)
        {
#if NETFX
            return t.IsPublic;
#else
            return t.GetTypeInfo().IsPublic;
#endif
        }
    }
}
