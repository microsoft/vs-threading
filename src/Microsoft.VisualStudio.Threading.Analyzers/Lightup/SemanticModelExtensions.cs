namespace Microsoft.VisualStudio.Threading.Analyzers.Lightup
{
    using System;
    using Microsoft.CodeAnalysis;

    internal static class SemanticModelExtensions
    {
        private static readonly Func<SemanticModel, int, NullableContext> GetNullableContextFunc
            = LightupHelpers.CreateAccessorWithArgument<SemanticModel, int, NullableContext>(typeof(SemanticModel), "semanticModel", typeof(int), "position", nameof(GetNullableContext), fallbackResult: NullableContext.Disabled);

        public static NullableContext GetNullableContext(this SemanticModel semanticModel, int position)
            => GetNullableContextFunc(semanticModel, position);
    }
}
