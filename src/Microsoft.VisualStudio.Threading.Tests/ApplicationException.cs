#if NETCOREAPP1_0

namespace Microsoft.VisualStudio.Threading.Tests
{
    public class ApplicationException : System.Exception
    {
        public ApplicationException()
        {
        }

        public ApplicationException(string message)
            : base(message)
        {
        }

        public ApplicationException(string message, System.Exception inner)
            : base(message, inner)
        {
        }
    }
}

#endif
