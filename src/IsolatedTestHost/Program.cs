namespace IsolatedTestHost
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length != 3)
            {
                return (int)ExitCodes.UnexpectedCommandLineArgs;
            }

            string assemblyFile = args[0];
            string testClassName = args[1];
            string testMethodName = args[2];

            return (int)MyMain(assemblyFile, testClassName, testMethodName);
        }

        private static ExitCodes MyMain(string assemblyFile, string testClassName, string testMethodName)
        {
            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(assemblyFile);
            }
            catch (FileNotFoundException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return ExitCodes.AssemblyNotFound;
            }

            Type testClass = assembly.GetType(testClassName);
            if (testClass == null)
            {
                return ExitCodes.TestClassNotFound;
            }

            MethodInfo testMethod = testClass.GetRuntimeMethod(testMethodName, Type.EmptyTypes);
            if (testMethod == null)
            {
                return ExitCodes.TestMethodNotFound;
            }

            bool fact = testMethod.GetCustomAttributesData().Any(a => a.AttributeType.Name == "FactAttribute");
            bool skippableFact = testMethod.GetCustomAttributesData().Any(a => a.AttributeType.Name == "SkippableFactAttribute");
            if (fact || skippableFact)
            {
                return ExecuteTest(testClass, testMethod);
            }

            bool stafact = testMethod.GetCustomAttributesData().Any(a => a.AttributeType.Name == "StaFactAttribute");
            if (stafact)
            {
                ExitCodes result = ExitCodes.TestFailed;
                var testThread = new Thread(() =>
                {
                    result = ExecuteTest(testClass, testMethod);
                });
                testThread.SetApartmentState(ApartmentState.STA);
                testThread.Start();
                testThread.Join();
                return result;
            }

            return ExitCodes.TestNotSupported;
        }

        private static ExitCodes ExecuteTest(Type testClass, MethodInfo testMethod)
        {
            try
            {
                var ctorWithLogger = testClass.GetConstructors().FirstOrDefault(
                    ctor => ctor.GetParameters().Length == 1 && ctor.GetParameters()[0].ParameterType.IsAssignableFrom(typeof(TestOutputHelper)));
                var ctorDefault = testClass.GetConstructor(Type.EmptyTypes);
                object testClassInstance =
                    ctorWithLogger?.Invoke(new object[] { new TestOutputHelper() }) ??
                    ctorDefault?.Invoke(Type.EmptyTypes);
                if (testClassInstance == null)
                {
                    return ExitCodes.TestNotSupported;
                }

                object result = testMethod.Invoke(testClassInstance, Type.EmptyTypes);
                if (result is Task resultTask)
                {
                    resultTask.GetAwaiter().GetResult();
                }

                if (testClassInstance is IDisposable disposableTestClass)
                {
                    disposableTestClass.Dispose();
                }

                return ExitCodes.TestPassed;
            }
            catch (Exception ex)
            {
                if (ex.GetType().Name == "SkipException")
                {
                    return ExitCodes.TestSkipped;
                }

                Console.Error.WriteLine("Test failed.");
                Console.Error.WriteLine(ex);
                return ExitCodes.TestFailed;
            }
        }
    }
}
