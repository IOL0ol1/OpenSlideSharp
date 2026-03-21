using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Text;

 
[Generator]
public sealed class SnakeCaseToPascalCaseGenerator : IIncrementalGenerator
{
    public static readonly DiagnosticDescriptor InfoDescriptor = new DiagnosticDescriptor(
        id: "LVSG001",
        title: "OpenSlide SourceGenerator Info",
        messageFormat: "{0}",
        category: "OpenSlide.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterSourceOutput(context.CompilationProvider, static (spc, _) =>
        {
            spc.AddSource("Utf8StringMarshaler.g.cs", SourceText.From(utf8MarshalerSource, Encoding.UTF8));
        });

        // Process additional files first, as they have higher priority than syntax trees and can provide better diagnostics.
        var nativeFiles = context.AdditionalTextsProvider
            .Where(static file => Path.GetFileName(file.Path)
                .StartsWith("OpenSlide.Interop.cs", StringComparison.OrdinalIgnoreCase));

        context.RegisterSourceOutput(nativeFiles, static (spc, additionalFile) =>
        {
            ProcessInputFile(spc, additionalFile.GetText(spc.CancellationToken), additionalFile.Path);
        });

        // Also process syntax trees to catch any files that might not be included as additional files, providing diagnostics for them.
        var syntaxProvider = context.CompilationProvider
            .SelectMany(static (compilation, ct) =>
                compilation.SyntaxTrees.Where(st =>
                    Path.GetFileName(st.FilePath)
                        .StartsWith("OpenSlide.Interop.cs", StringComparison.OrdinalIgnoreCase)));

        context.RegisterSourceOutput(syntaxProvider, static (spc, syntaxTree) =>
        {
            ProcessInputFile(spc, syntaxTree.GetText(spc.CancellationToken), syntaxTree.FilePath);
        });
    }

    private static void ProcessInputFile(SourceProductionContext spc, SourceText? sourceText, string filePath)
    {
        try
        {
            if (sourceText == null) return;
            var fileName = Path.GetFileName(filePath);
            spc.ReportDiagnostic(Diagnostic.Create(InfoDescriptor, Location.None, $"Processing: {fileName}"));

            var root = CSharpSyntaxTree.ParseText(sourceText,
                cancellationToken: spc.CancellationToken, path: filePath)
                .GetCompilationUnitRoot(spc.CancellationToken);

            var collector = new RenameCollector();
            collector.Visit(root);

            var rewriter = new RenameVisitor(collector.GlobalRenames);
            var newRoot = rewriter.Visit(root);

            if (newRoot == root)
            {
                spc.ReportDiagnostic(Diagnostic.Create(InfoDescriptor, Location.None,
                    $"No changes for {fileName}"));
                return;
            }

            var cu = (CompilationUnitSyntax)newRoot;
            var usings = cu.Usings;
            MemberDeclarationSyntax namespaceWrapped;

            var existingNs = cu.Members.OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
            if (existingNs != null)
            {
                namespaceWrapped = existingNs
                    .WithName(SyntaxFactory.ParseName(RootNamespace))
                    .WithMembers(existingNs.Members)
                    .NormalizeWhitespace();
            }
            else
            {
                namespaceWrapped = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(RootNamespace))
                    .WithMembers(cu.Members)
                    .NormalizeWhitespace();
            }

            var finalCompilationUnit = SyntaxFactory.CompilationUnit()
                .WithUsings(usings)
                .WithMembers(SyntaxFactory.SingletonList(namespaceWrapped))
                .NormalizeWhitespace();

            // 输出
            var hintName = $"{Path.GetFileNameWithoutExtension(fileName)}.g.cs";
            spc.AddSource(hintName, SourceText.From(finalCompilationUnit.ToFullString(), Encoding.UTF8));

            spc.ReportDiagnostic(Diagnostic.Create(InfoDescriptor, Location.None,
                $"Generated: {hintName} ({collector.GlobalRenames.Count} renames)"));
        }
        catch (Exception ex)
        {
            spc.ReportDiagnostic(Diagnostic.Create(InfoDescriptor, Location.None,
                $"Error processing {filePath}: {ex.Message}"));
        }
    }


    public const string RootNamespace = @"OpenSlideSharp";


    private const string utf8MarshalerSource = @"using System;
using System.Runtime.InteropServices;
using System.Text;

namespace " + RootNamespace + @"
{
    public class Utf8StringMarshaler : ICustomMarshaler
    {
        private static readonly Utf8StringMarshaler Instance = new Utf8StringMarshaler();

        public static ICustomMarshaler GetInstance(string cookie) => Instance;

        public void CleanUpManagedData(object ManagedObj) { }

        public void CleanUpNativeData(IntPtr pNativeData)
        {
            if (pNativeData != IntPtr.Zero)
                Marshal.FreeHGlobal(pNativeData);
        }

        public int GetNativeDataSize() => -1;

        public IntPtr MarshalManagedToNative(object ManagedObj)
        {
            if (ManagedObj is string value)
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                var buffer = Marshal.AllocHGlobal(bytes.Length + 1);
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
                Marshal.WriteByte(buffer, bytes.Length, 0);
                return buffer;
            }
            return IntPtr.Zero; 
        }

        public unsafe object MarshalNativeToManaged(IntPtr pNativeData)
        {
            if (pNativeData == IntPtr.Zero)
                return null;
            int len = 0;
            while (Marshal.ReadByte(pNativeData, len) != 0) len++;
            return Encoding.UTF8.GetString((byte*)pNativeData, len);
        }
    }
}";
}

/// <summary>
/// Syntax walker that collects rename mappings needed to convert OpenSlide identifiers
/// from snake_case / C-style naming to .NET PascalCase.
/// </summary>
internal sealed class RenameCollector : CSharpSyntaxWalker
{
    /// <summary>Maps each original identifier to its renamed counterpart.</summary>
    public Dictionary<string, string> GlobalRenames { get; } = new(StringComparer.Ordinal);

    public override void VisitStructDeclaration(StructDeclarationSyntax node)
    {
        Add(node.Identifier.Text, NameConverter.ConvertTypeName(node.Identifier.Text));
        base.VisitStructDeclaration(node);
    }

    public override void VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        Add(node.Identifier.Text, NameConverter.ConvertClassName(node.Identifier.Text));
        base.VisitClassDeclaration(node);
    }

    /// <summary>
    /// Records PascalCase renames for the enum type and all its members,
    /// stripping the common enum-name prefix from each member.
    /// </summary>
    public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
    {
        var enumName = node.Identifier.Text;
        Add(enumName, NameConverter.ConvertTypeName(enumName));

        var memberNames = node.Members.Select(m => m.Identifier.Text).ToList();
        var skipSegments = NameConverter.SkipEnumSegment(enumName, memberNames);

        foreach (var member in node.Members)
            Add(member.Identifier.Text, NameConverter.ConvertEnumMemberName(member.Identifier.Text, skipSegments));

        base.VisitEnumDeclaration(node);
    }

    public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
    {
        Add(node.Identifier.Text, NameConverter.ConvertTypeName(node.Identifier.Text));
        base.VisitDelegateDeclaration(node);
    }

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        Add(node.Identifier.Text, NameConverter.ConvertMethodName(node.Identifier.Text));
        base.VisitMethodDeclaration(node);
    }

    public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        if (node.Parent is StructDeclarationSyntax)
            Add(node.Identifier.Text, NameConverter.ConvertStructFieldName(node.Identifier.Text));

        base.VisitPropertyDeclaration(node);
    }

    public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        // Struct fields are handled via BuildStructFieldMap to avoid naming conflicts.
        if (node.Parent is StructDeclarationSyntax)
        {
            base.VisitFieldDeclaration(node);
            return;
        }

        foreach (var variable in node.Declaration.Variables)
        {
            var target = node.Modifiers.Any(SyntaxKind.ConstKeyword)
                ? NameConverter.ConvertConstantFieldName(variable.Identifier.Text)
                : variable.Identifier.Text.ToCamelCase();

            Add(variable.Identifier.Text, target);
        }

        base.VisitFieldDeclaration(node);
    }

    /// <summary>Adds an oldName to newName entry if they differ and the key is not already present.</summary>
    private void Add(string oldName, string newName)
    {
        if (string.IsNullOrEmpty(oldName) || string.Equals(oldName, newName, StringComparison.Ordinal))
            return;

        if (!GlobalRenames.ContainsKey(oldName))
        {
            GlobalRenames.Add(oldName, newName);
        }
    }
}

/// <summary>
/// Syntax rewriter that applies the rename mappings collected by <see cref="RenameCollector"/>
/// to produce a new AST using .NET PascalCase / camelCase identifiers.
/// </summary>
public sealed class RenameVisitor : CSharpSyntaxRewriter
{
    private readonly Dictionary<string, string> _globalRenames;
    // Per-method parameter rename scope (camelCase), pushed/popped around method bodies.
    private readonly Stack<Dictionary<string, string>> _parameterScopeRenames = new();
    // Per-struct field rename scope, pushed/popped around struct bodies.
    private readonly Stack<Dictionary<string, string>> _structFieldScopeRenames = new();

    public RenameVisitor(Dictionary<string, string> globalRenames) => _globalRenames = globalRenames;

    private static SyntaxTokenList EnsurePartial(SyntaxTokenList modifiers) =>
        modifiers.Any(static m => m.IsKind(SyntaxKind.PartialKeyword))
            ? modifiers
            : modifiers.Add(SyntaxFactory.Token(SyntaxKind.PartialKeyword));

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
    {
        var fieldMap = BuildStructFieldMap(node);
        var updated = node
            .WithModifiers(EnsurePartial(node.Modifiers))
            .WithIdentifier(SyntaxFactory.Identifier(NameConverter.ConvertTypeName(node.Identifier.Text)));

        _structFieldScopeRenames.Push(fieldMap);
        try { return base.VisitStructDeclaration(updated); }
        finally { _structFieldScopeRenames.Pop(); }
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var updated = node
            .WithModifiers(EnsurePartial(node.Modifiers))
            .WithIdentifier(SyntaxFactory.Identifier(NameConverter.ConvertClassName(node.Identifier.Text)));
        return base.VisitClassDeclaration(updated);
    }

    public override SyntaxNode? VisitEnumDeclaration(EnumDeclarationSyntax node)
    {
        var enumName = node.Identifier.Text;
        var memberNames = node.Members.Select(m => m.Identifier.Text).ToList();
        var skipSegments = NameConverter.SkipEnumSegment(enumName, memberNames);

        var updated = node
            .WithIdentifier(SyntaxFactory.Identifier(NameConverter.ConvertTypeName(enumName)))
            .WithMembers(SyntaxFactory.SeparatedList(node.Members.Select(m =>
                m.WithIdentifier(SyntaxFactory.Identifier(
                    NameConverter.ConvertEnumMemberName(m.Identifier.Text, skipSegments))))));

        return base.VisitEnumDeclaration(updated);
    }

    public override SyntaxNode? VisitDelegateDeclaration(DelegateDeclarationSyntax node)
    {
        var parameterMap = CreateParameterMap(node.ParameterList.Parameters);
        var updated = node
            .WithIdentifier(SyntaxFactory.Identifier(NameConverter.ConvertTypeName(node.Identifier.Text)))
            .WithParameterList(RenameParameterList(node.ParameterList, parameterMap, applyUtf8Marshaling: false));

        return base.VisitDelegateDeclaration(updated);
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var originalName = node.Identifier.Text;
        var parameterMap = CreateParameterMap(node.ParameterList.Parameters);
        var updated = node
            .WithIdentifier(SyntaxFactory.Identifier(NameConverter.ConvertMethodName(originalName)))
            .WithParameterList(RenameParameterList(node.ParameterList, parameterMap, applyUtf8Marshaling: true));

        if (IsUtf8StringReturn(node.ReturnType, node.AttributeLists))
        {
            updated = updated
                .WithReturnType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)))
                .WithAttributeLists(EnsureMarshalAsUtf8Attribute(updated.AttributeLists, forReturnValue: true));
        }

        updated = EnsureDllImportEntryPoint(updated, originalName);

        _parameterScopeRenames.Push(parameterMap);
        try { return base.VisitMethodDeclaration(updated); }
        finally { _parameterScopeRenames.Pop(); }
    }

    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        if (node.Parent is StructDeclarationSyntax)
            return base.VisitPropertyDeclaration(
                node.WithIdentifier(SyntaxFactory.Identifier(NameConverter.ConvertStructFieldName(node.Identifier.Text))));

        return base.VisitPropertyDeclaration(node);
    }

    public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        var newVariables = node.Declaration.Variables.Select(v =>
        {
            if (_structFieldScopeRenames.Count > 0 && _structFieldScopeRenames.Peek().TryGetValue(v.Identifier.Text, out var structRenamed))
                return v.WithIdentifier(SyntaxFactory.Identifier(structRenamed));

            if (_globalRenames.TryGetValue(v.Identifier.Text, out var renamed))
                return v.WithIdentifier(SyntaxFactory.Identifier(renamed));

            return v;
        });

        return base.VisitFieldDeclaration(
            node.WithDeclaration(node.Declaration.WithVariables(SyntaxFactory.SeparatedList(newVariables))));
    }

    /// <summary>
    /// Resolves identifier references: checks the parameter scope stack first (camelCase),
    /// then the global rename map (PascalCase).
    /// The special token <c>uncheckednull</c> is expanded to <c>unchecked((IntPtr)0)</c>.
    /// </summary>
    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        var name = node.Identifier.Text;

        if (string.Equals(name, "uncheckednull", StringComparison.Ordinal))
            return SyntaxFactory.ParseExpression("IntPtr.Zero");

        foreach (var scope in _parameterScopeRenames)
            if (scope.TryGetValue(name, out var paramRenamed))
                return node.WithIdentifier(SyntaxFactory.Identifier(paramRenamed));

        foreach (var scope in _structFieldScopeRenames)
            if (scope.TryGetValue(name, out var fieldRenamed))
                return node.WithIdentifier(SyntaxFactory.Identifier(fieldRenamed));

        // Inline type references like OpenSlide_media_t that were not captured in GlobalRenames.
        if (name.StartsWith("OpenSlide_", StringComparison.OrdinalIgnoreCase) && name.EndsWith("_t", StringComparison.Ordinal))
            return node.WithIdentifier(SyntaxFactory.Identifier(NameConverter.ConvertTypeName(name)));

        // Rename members accessed via pointer (->).
        if (node.Parent is MemberAccessExpressionSyntax memberAccess &&
            memberAccess.IsKind(SyntaxKind.PointerMemberAccessExpression) &&
            memberAccess.Name == node)
        {
            var memberRenamed = NameConverter.ConvertStructFieldName(name);
            if (!string.Equals(memberRenamed, name, StringComparison.Ordinal))
                return node.WithIdentifier(SyntaxFactory.Identifier(memberRenamed));
        }

        if (_globalRenames.TryGetValue(name, out var renamed))
            return node.WithIdentifier(SyntaxFactory.Identifier(renamed));

        return base.VisitIdentifierName(node);
    }

    /// <summary>Builds a camelCase rename map for the given parameter list.</summary>
    private static Dictionary<string, string> CreateParameterMap(SeparatedSyntaxList<ParameterSyntax> parameters)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in parameters)
        {
            var oldName = p.Identifier.Text;
            var newName = oldName.ToCamelCase();
            if (!string.Equals(oldName, newName, StringComparison.Ordinal) && !map.ContainsKey(oldName))
            {
                map.Add(oldName, newName);
            }
        }
        return map;
    }

    /// <summary>Returns a new parameter list with identifiers renamed per <paramref name="parameterMap"/>.</summary>
    private static ParameterListSyntax RenameParameterList(
        ParameterListSyntax parameterList,
        Dictionary<string, string> parameterMap,
        bool applyUtf8Marshaling)
    {
        var renamed = parameterList.Parameters.Select(p =>
        {
            var param = parameterMap.TryGetValue(p.Identifier.Text, out var newName)
                ? p.WithIdentifier(SyntaxFactory.Identifier(newName))
                : p;

            if (applyUtf8Marshaling && IsUtf8StringParameter(param))
            {
                param = param
                    .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)))
                    .WithAttributeLists(EnsureMarshalAsUtf8Attribute(param.AttributeLists));
            }

            return param;
        });

        return parameterList.WithParameters(SyntaxFactory.SeparatedList(renamed));
    }

    /// <summary>
    /// Ensures the DllImport attribute on <paramref name="method"/> has an
    /// EntryPoint argument set to <paramref name="originalMethodName"/>.
    /// </summary>
    private static MethodDeclarationSyntax EnsureDllImportEntryPoint(MethodDeclarationSyntax method, string originalMethodName)
    {
        if (string.IsNullOrEmpty(originalMethodName)) return method;

        var attributeLists = method.AttributeLists;

        for (int listIndex = 0; listIndex < attributeLists.Count; listIndex++)
        {
            var attributeList = attributeLists[listIndex];
            var attributes = attributeList.Attributes;

            for (int attrIndex = 0; attrIndex < attributes.Count; attrIndex++)
            {
                var attr = attributes[attrIndex];
                var attrName = attr.Name.ToString();
                if (!attrName.EndsWith("DllImport", StringComparison.Ordinal) &&
                    !attrName.EndsWith("DllImportAttribute", StringComparison.Ordinal))
                    continue;

                var argumentList = attr.ArgumentList ?? SyntaxFactory.AttributeArgumentList();
                var args = argumentList.Arguments;
                for (int i = 0; i < args.Count; i++)
                {
                    var arg = args[i];
                    if (arg.NameEquals != null &&
                        string.Equals(arg.NameEquals.Name.Identifier.Text, "EntryPoint", StringComparison.Ordinal))
                    {
                        // EntryPoint already exists, do not modify
                        return method;
                    }
                }

                // EntryPoint not found, add it
                args = args.Add(SyntaxFactory.AttributeArgument(
                    nameEquals: SyntaxFactory.NameEquals("EntryPoint"),
                    nameColon: null,
                    expression: SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression,
                        SyntaxFactory.Literal(originalMethodName))));

                attributes = attributes.Replace(attr, attr.WithArgumentList(argumentList.WithArguments(args)));
                attributeLists = attributeLists.Replace(attributeList, attributeList.WithAttributes(attributes));
                return method.WithAttributeLists(attributeLists);
            }
        }

        return method;
    }

    /// <summary>
    /// Builds a field-rename map for a struct, skipping renames that would collide
    /// with existing member names.
    /// </summary>
    private static Dictionary<string, string> BuildStructFieldMap(StructDeclarationSyntax node)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var usedNames = new HashSet<string>(StringComparer.Ordinal);

        // Collect all currently used names to detect collisions.
        foreach (var member in node.Members)
        {
            switch (member)
            {
                case BaseTypeDeclarationSyntax t: usedNames.Add(t.Identifier.Text); break;
                case DelegateDeclarationSyntax d: usedNames.Add(d.Identifier.Text); break;
                case MethodDeclarationSyntax m: usedNames.Add(m.Identifier.Text); break;
                case PropertyDeclarationSyntax p: usedNames.Add(p.Identifier.Text); break;
                case EventDeclarationSyntax e: usedNames.Add(e.Identifier.Text); break;
                case FieldDeclarationSyntax f:
                    foreach (var v in f.Declaration.Variables) usedNames.Add(v.Identifier.Text);
                    break;
            }
        }

        foreach (var field in node.Members.OfType<FieldDeclarationSyntax>())
        {
            foreach (var variable in field.Declaration.Variables)
            {
                var oldName = variable.Identifier.Text;
                var newName = NameConverter.ConvertStructFieldName(oldName);

                if (string.Equals(oldName, newName, StringComparison.Ordinal) || usedNames.Contains(newName))
                    continue;

                usedNames.Remove(oldName);
                usedNames.Add(newName);
                map[oldName] = newName;
            }
        }

        return map;
    }

    private static bool IsUtf8StringParameter(ParameterSyntax parameter) =>
        parameter.Type is PointerTypeSyntax { ElementType: PredefinedTypeSyntax pt } &&
        pt.Keyword.IsKind(SyntaxKind.ByteKeyword) &&
        HasConstCharNativeTypeName(parameter.AttributeLists);

    private static bool IsUtf8StringReturn(TypeSyntax returnType, SyntaxList<AttributeListSyntax> attributeLists) =>
        returnType is PointerTypeSyntax { ElementType: PredefinedTypeSyntax pt } &&
        pt.Keyword.IsKind(SyntaxKind.ByteKeyword) &&
        HasConstCharNativeTypeName(attributeLists, returnOnly: true);

    private static bool HasConstCharNativeTypeName(SyntaxList<AttributeListSyntax> attributeLists, bool returnOnly = false)
    {
        foreach (var attributeList in attributeLists)
        {
            if (returnOnly && !string.Equals(attributeList.Target?.Identifier.Text, "return", StringComparison.Ordinal))
                continue;

            foreach (var attribute in attributeList.Attributes)
            {
                var attrName = attribute.Name.ToString();
                if (!string.Equals(attrName, "NativeTypeName", StringComparison.Ordinal) &&
                    !string.Equals(attrName, "NativeTypeNameAttribute", StringComparison.Ordinal))
                    continue;

                if (attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression
                    is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } lit &&
                    lit.Token.ValueText.IndexOf("const char", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }

        return false;
    }

    private static SyntaxList<AttributeListSyntax> EnsureMarshalAsUtf8Attribute(
        SyntaxList<AttributeListSyntax> attributeLists,
        bool forReturnValue = false)
    {
        // Return early if MarshalAs is already present.
        foreach (var attributeList in attributeLists)
        {
            if (forReturnValue && !string.Equals(attributeList.Target?.Identifier.Text, "return", StringComparison.Ordinal))
                continue;

            if (attributeList.Attributes.Any(a =>
                    string.Equals(a.Name.ToString(), "MarshalAs", StringComparison.Ordinal) ||
                    string.Equals(a.Name.ToString(), "MarshalAsAttribute", StringComparison.Ordinal)))
                return attributeLists;
        }

        var marshalAs = SyntaxFactory
            .Attribute(SyntaxFactory.ParseName("global::System.Runtime.InteropServices.MarshalAs"))
            .WithArgumentList(SyntaxFactory.AttributeArgumentList(SyntaxFactory.SeparatedList(new[]
            {
                SyntaxFactory.AttributeArgument(
                    SyntaxFactory.ParseExpression("global::System.Runtime.InteropServices.UnmanagedType.CustomMarshaler")),
                SyntaxFactory.AttributeArgument(
                    nameEquals: SyntaxFactory.NameEquals("MarshalTypeRef"),
                    nameColon: null,
                    expression: SyntaxFactory.TypeOfExpression(
                        SyntaxFactory.ParseTypeName($"global::{SnakeCaseToPascalCaseGenerator.RootNamespace}.Utf8StringMarshaler")))
            })));

        var newList = SyntaxFactory.AttributeList(
            target: forReturnValue
                ? SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(SyntaxKind.ReturnKeyword))
                : null,
            attributes: SyntaxFactory.SingletonSeparatedList(marshalAs));

        return attributeLists.Add(newList);
    }
}

/// <summary>Utility methods for converting C-style snake_case names to .NET naming conventions.</summary>
internal static class NameConverter
{

    public static string ConvertTypeName(string name) =>
        name.RemoveSuffix("_t").ToPascalCase();
    public static string ConvertMethodName(string name) =>
        name.ToPascalCase();

    public static string ConvertConstantFieldName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (name.All(ch => char.IsUpper(ch) || char.IsDigit(ch) || ch == '_'))
            return name;
        return name.ToPascalCase();
    }

    public static string ConvertClassName(string name) =>
        string.Equals(name, "openslide", StringComparison.OrdinalIgnoreCase) ? "OpenSlide" : ConvertTypeName(name);


    public static string ConvertStructFieldName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        if (name.StartsWith("f_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("i_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("b_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("p_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("psz_", StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring(name.IndexOf('_') + 1);
        }

        return string.IsNullOrEmpty(name) ? name : name.ToPascalCase();
    }


    public static string ConvertEnumMemberName(string memberName, int skipSegments)
    {
        var memberSegments = memberName.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join("_", memberSegments.Skip(skipSegments)).ToPascalCase();
    }

    public static int SkipEnumSegment(string enumName, IEnumerable<string> memberNames)
    {
        var enumSegments = enumName.RemoveSuffix("_t").Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
        var skipSegment = int.MaxValue;
        foreach (var memberName in memberNames)
        {
            var memberSegments = memberName.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            var sameSegment = enumSegments.Zip(memberSegments, (e, m) => e.Equals(m, StringComparison.OrdinalIgnoreCase))
                .TakeWhile(same => same).Count();

            // If the first different segment starts with a digit, it's likely a numeric suffix and should be skipped as well.
            if (sameSegment > 0 && char.IsDigit(memberSegments[sameSegment].FirstOrDefault()))
                sameSegment = sameSegment - 1;

            if(sameSegment < skipSegment)
                skipSegment = sameSegment;
        }

        return skipSegment == int.MaxValue ? 0 : skipSegment;
    }

    public static string RemovePrefix(this string value, string prefix, StringComparison comparison = StringComparison.Ordinal)
    {
        if (value.StartsWith(prefix, comparison))
            return value.Substring(prefix.Length);
        return value;
    }

    public static string AddPrefix(this string value, string prefix)
    {
        return prefix + value;
    }

    public static string AddSuffix(this string value, string suffix)
    {
        return value + suffix;
    }

    public static string RemoveSuffix(this string value, string suffix, StringComparison comparison = StringComparison.Ordinal)
    {
        if (value.EndsWith(suffix, comparison))
            return value.Substring(0, value.Length - suffix.Length);
        return value;
    }

    public static string ToPascalCase(this string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var parts = value.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return value;
        if (parts.Length == 1) return char.ToUpperInvariant(value[0]) + value.Substring(1);
        var converted = string.Concat(parts.Select(ToPascalCase));
        return converted.Length > 0 && char.IsDigit(converted[0]) ? "_" + converted : converted;
    }

    public static string ToCamelCase(this string value)
    {
        var pascal = value.ToPascalCase();
        return string.IsNullOrEmpty(pascal) ? pascal : char.ToLowerInvariant(pascal[0]) + pascal.Substring(1);
    }
}