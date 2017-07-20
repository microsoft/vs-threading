namespace IsolatedTestHost
{
    using System;
    using Xunit.Abstractions;

    internal class TestOutputHelper : ITestOutputHelper
    {
        public void WriteLine(string message)
        {
            Console.WriteLine(message);
        }

        public void WriteLine(string format, params object[] args)
        {
            Console.WriteLine(format, args);
        }
    }
}
