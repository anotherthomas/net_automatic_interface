﻿using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AutomaticInterface;

public static class Builder
{
    private const string InheritDoc = "/// <inheritdoc />"; // we use inherit doc because that should be able to fetch documentation from base classes.

    public static string BuildInterfaceFor(ITypeSymbol typeSymbol)
    {
        if (
            typeSymbol.DeclaringSyntaxReferences.First().GetSyntax()
            is not ClassDeclarationSyntax classSyntax
        )
        {
            return string.Empty;
        }

        var namespaceName = typeSymbol.ContainingNamespace.ToDisplayString();

        var interfaceName = $"I{classSyntax.GetClassName()}";

        var interfaceGenerator = new InterfaceBuilder(namespaceName, interfaceName);

        interfaceGenerator.AddUsings(GetUsings(typeSymbol));
        interfaceGenerator.AddClassDocumentation(GetDocumentationForClass(classSyntax));
        interfaceGenerator.AddGeneric(GetGeneric(classSyntax));
        AddPropertiesToInterface(typeSymbol, interfaceGenerator);
        AddMethodsToInterface(typeSymbol, interfaceGenerator);
        AddEventsToInterface(typeSymbol, interfaceGenerator);

        var generatedCode = interfaceGenerator.Build();

        return generatedCode;
    }

    private static void AddMethodsToInterface(
        ITypeSymbol classSymbol,
        InterfaceBuilder codeGenerator
    )
    {
        classSymbol
            .GetAllMembers()
            .Where(x => x.Kind == SymbolKind.Method)
            .OfType<IMethodSymbol>()
            .Where(x => x.DeclaredAccessibility == Accessibility.Public)
            .Where(x => x.MethodKind == MethodKind.Ordinary)
            .Where(x => !x.IsStatic)
            .Where(x => x.ContainingType.Name != nameof(Object))
            .ToList()
            .ForEach(method =>
            {
                AddMethod(codeGenerator, method);
            });
    }

    private static void AddMethod(InterfaceBuilder codeGenerator, IMethodSymbol method)
    {
        var returnType = method.ReturnType;
        var name = method.Name;

        var hasNullableParameter = method.Parameters.Any(x =>
            x.NullableAnnotation == NullableAnnotation.Annotated
        );
        var hasNullableReturn =
            method.ReturnType.NullableAnnotation == NullableAnnotation.Annotated;
        if (hasNullableParameter || hasNullableReturn)
        {
            codeGenerator.HasNullable = true;
        }

        var paramResult = new HashSet<string>();
        method.Parameters.Select(GetMethodSignature).ToList().ForEach(x => paramResult.Add(x));

        var typedArgs = method
            .TypeParameters.Select(arg => (arg.ToDisplayString(), arg.GetWhereStatement()))
            .ToList();
        codeGenerator.AddMethodToInterface(
            name,
            returnType.ToDisplayString(),
            InheritDoc,
            paramResult,
            typedArgs
        );
    }

    private static void AddEventsToInterface(
        ITypeSymbol classSymbol,
        InterfaceBuilder codeGenerator
    )
    {
        classSymbol
            .GetAllMembers()
            .Where(x => x.Kind == SymbolKind.Event)
            .OfType<IEventSymbol>()
            .Where(x => x.DeclaredAccessibility == Accessibility.Public)
            .Where(x => !x.IsStatic)
            .ToList()
            .ForEach(evt =>
            {
                var type = evt.Type;
                var name = evt.Name;

                codeGenerator.AddEventToInterface(name, type.ToDisplayString(), InheritDoc);
            });
    }

    private static string GetMethodSignature(IParameterSymbol x)
    {
        if (!x.HasExplicitDefaultValue)
        {
            return $"{x.Type.ToDisplayString()} {x.Name}";
        }

        var optionalValue = x.ExplicitDefaultValue switch
        {
            string => $" = \"{x.ExplicitDefaultValue}\"",
            // struct
            null when x.Type.IsValueType => $" = default({x.Type})",
            null => " = null",
            _ => $" = {x.ExplicitDefaultValue}"
        };
        return $"{x.Type.ToDisplayString()} {x.Name}{optionalValue}";
    }

    private static void AddPropertiesToInterface(
        ITypeSymbol classSymbol,
        InterfaceBuilder interfaceGenerator
    )
    {
        classSymbol
            .GetAllMembers()
            .Where(x => x.Kind == SymbolKind.Property)
            .OfType<IPropertySymbol>()
            .Where(x => x.DeclaredAccessibility == Accessibility.Public)
            .Where(x => !x.IsStatic)
            .Where(x => !x.IsIndexer)
            .ToList()
            .ForEach(prop =>
            {
                var type = prop.Type;

                var name = prop.Name;
                var hasGet = prop.GetMethod?.DeclaredAccessibility == Accessibility.Public;
                var hasSet = prop.SetMethod?.DeclaredAccessibility == Accessibility.Public;

                interfaceGenerator.AddPropertyToInterface(
                    name,
                    type.ToDisplayString(),
                    hasGet,
                    hasSet,
                    InheritDoc
                );
            });
    }

    private static string GetDocumentationForClass(CSharpSyntaxNode classSyntax)
    {
        if (!classSyntax.HasLeadingTrivia)
        {
            // no documentation
            return string.Empty;
        }

        SyntaxKind[] docSyntax =
        [
            SyntaxKind.DocumentationCommentExteriorTrivia,
            SyntaxKind.EndOfDocumentationCommentToken,
            SyntaxKind.MultiLineDocumentationCommentTrivia,
            SyntaxKind.SingleLineDocumentationCommentTrivia
        ];

        var trivia = classSyntax
            .GetLeadingTrivia()
            .Where(x => docSyntax.Contains(x.Kind()))
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.ToFullString()));

        return trivia.ToFullString().Trim();
    }

    private static IEnumerable<string> GetUsings(ISymbol classSymbol)
    {
        var allUsings = SyntaxFactory.List<UsingDirectiveSyntax>();
        foreach (var syntaxRef in classSymbol.DeclaringSyntaxReferences)
        {
            allUsings = syntaxRef
                .GetSyntax()
                .Ancestors(false)
                .Aggregate(
                    allUsings,
                    (current, parent) =>
                        parent switch
                        {
                            NamespaceDeclarationSyntax ndSyntax
                                => current.AddRange(ndSyntax.Usings),
                            CompilationUnitSyntax cuSyntax => current.AddRange(cuSyntax.Usings),
                            _ => current
                        }
                );
        }

        return [..allUsings.Select(x => x.ToString())];
    }

    private static string GetGeneric(TypeDeclarationSyntax classSyntax)
    {
        if (classSyntax.TypeParameterList?.Parameters.Count == 0)
        {
            return string.Empty;
        }

        var formattedGeneric =
            $"{classSyntax.TypeParameterList?.ToFullString().Trim()} {classSyntax.ConstraintClauses}".Trim();

        return formattedGeneric;
    }
}