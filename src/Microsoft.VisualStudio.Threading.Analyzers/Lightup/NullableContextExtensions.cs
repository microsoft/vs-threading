namespace Microsoft.VisualStudio.Threading.Analyzers.Lightup
{
    internal static class NullableContextExtensions
    {
        /// <summary>
        /// Returns whether nullable warnings are enabled for this context.
        /// </summary>
        public static bool WarningsEnabled(this NullableContext context) =>
            IsFlagSet(context, NullableContext.WarningsEnabled);

        /// <summary>
        /// Returns whether nullable annotations are enabled for this context.
        /// </summary>
        public static bool AnnotationsEnabled(this NullableContext context) =>
            IsFlagSet(context, NullableContext.AnnotationsEnabled);

        /// <summary>
        /// Returns whether the nullable warning state was inherited from the project default for this file type.
        /// </summary>
        public static bool WarningsInherited(this NullableContext context) =>
            IsFlagSet(context, NullableContext.WarningsContextInherited);

        /// <summary>
        /// Returns whether the nullable annotation state was inherited from the project default for this file type.
        /// </summary>
        public static bool AnnotationsInherited(this NullableContext context) =>
            IsFlagSet(context, NullableContext.AnnotationsContextInherited);

        private static bool IsFlagSet(NullableContext context, NullableContext flag) =>
            (context & flag) == flag;
    }
}
