/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace IsolatedTestHost
{
    /// <summary>
    /// The meanings of each exit code that may be returned from this process.
    /// </summary>
    public enum ExitCodes
    {
        /// <summary>
        /// The test executed and passed.
        /// </summary>
        TestPassed = 0,

        /// <summary>
        /// The test executed and failed.
        /// </summary>
        TestFailed,

        /// <summary>
        /// The test threw SkipException.
        /// </summary>
        TestSkipped,

        /// <summary>
        /// The test assembly could not be found.
        /// </summary>
        AssemblyNotFound,

        /// <summary>
        /// The test class could not be found.
        /// </summary>
        TestClassNotFound,

        /// <summary>
        /// The test method could not be found.
        /// </summary>
        TestMethodNotFound,

        /// <summary>
        /// The test class or test method took parameters that are not supported by this host.
        /// </summary>
        TestNotSupported,

        /// <summary>
        /// Too few or too many command line arguments passed to this process.
        /// </summary>
        UnexpectedCommandLineArgs,
    }
}
