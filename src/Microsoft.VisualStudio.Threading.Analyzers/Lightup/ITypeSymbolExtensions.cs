namespace Microsoft.VisualStudio.Threading.Analyzers.Lightup
{
    using System;
    using Microsoft.CodeAnalysis;

    internal static class ITypeSymbolExtensions
    {
        private static readonly Func<ITypeSymbol, NullableAnnotation> NullableAnnotationFunc
            = LightupHelpers.CreateSymbolPropertyAccessor<ITypeSymbol, NullableAnnotation>(typeof(ITypeSymbol), nameof(NullableAnnotation), fallbackResult: Lightup.NullableAnnotation.None);

        private static readonly Func<ITypeSymbol, NullableAnnotation, ITypeSymbol> WithNullableAnnotationFunc
            = LightupHelpers.CreateSymbolWithPropertyAccessor<ITypeSymbol, NullableAnnotation>(typeof(ITypeSymbol), nameof(NullableAnnotation), fallbackResult: Lightup.NullableAnnotation.None);

        public static NullableAnnotation NullableAnnotation(this ITypeSymbol typeSymbol)
            => NullableAnnotationFunc(typeSymbol);

        public static ITypeSymbol WithNullableAnnotation(this ITypeSymbol typeSymbol, NullableAnnotation nullableAnnotation)
            => WithNullableAnnotationFunc(typeSymbol, nullableAnnotation);
    }
}
