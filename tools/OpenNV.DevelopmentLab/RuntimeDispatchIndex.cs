using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Reads the runtime's dispatch code itself; nothing here is a list of supported
// names. From each dispatch root it collects every constant the dispatch
// compares its subject with (if/switch/pattern guards and registry lookups),
// following helpers that receive the subject. It does not execute guards, so
// argument-count clauses and owner availability remain unverified.
internal static class RuntimeDispatchIndex
{
    internal sealed record Site(string Owner, string File, int Line, string Guard, string Kind, bool Deferred, bool Rejected);
    internal sealed record Input(string File, string Sha256);
    internal sealed record Index(IReadOnlyDictionary<string, List<Site>> Names, IReadOnlyList<Input> Sources,
        IReadOnlyList<Site> DynamicGuards, IReadOnlySet<string> LiteralDeferrals);
    internal sealed record Root(string Owner, string File, string Method, params string[] Subjects);

    private const string ReferenceScripts = "runtime/src/World/Cells/FalloutReferenceScripts.cs";
    // Deferral fallback used only when no native session supplies host.Command;
    // the game session always supplies it, so it is not a gameplay deferral.
    internal const string HeadlessDeferral = "no-native-session-owner";

    // Every reached statement goes through the reference executor; quest scripts
    // use it through FalloutQuestScriptHost.ExecuteProgram. Its fallthrough is
    // the native session's command dispatch.
    internal static readonly Root[] Statements =
    [
        new("reference-result", ReferenceScripts, "Call", "operation", "command"),
        new("native-host", "runtime/src/Campaigns/NewVegas/Opening/RuntimeNativeOpeningStageDriver.Commands.cs",
            "ApplyNativeSourceCommand", "operation", "command"),
    ];
    internal static readonly Root[] Functions = [new("reference-function", ReferenceScripts, "FunctionFor", "operation", "name", "parts[^1]")];
    internal static readonly Root[] Conditions =
    [
        new("condition-runtime", "runtime/src/Content/FalloutConditionRuntime.cs", "EvaluateValue"),
        new("condition-runtime", "runtime/src/Content/FalloutConditionRuntime.cs", "EvaluateSubject"),
        new("platform", "runtime/src/Content/FalloutConditions.cs", "Evaluate"),
        new("npc-native", "runtime/src/World/Actors/RuntimeNativeNpc.Ai.cs", "EvaluateOwnAiCondition"),
        new("creature-native", "runtime/src/World/Actors/RuntimeNativeCreature.Ai.cs", "EvaluateNativeCondition"),
        new("player-native", "runtime/src/Campaigns/NewVegas/Opening/RuntimeNativeOpeningStageDriver.Conditions.cs", "EvaluatePlayerNativeCondition"),
    ];
    internal static readonly Root[] Packages =
    [
        new("npc", "runtime/src/World/Actors/RuntimeNativeNpc.Ai.cs", "AdvanceAi"),
        new("creature", "runtime/src/World/Actors/RuntimeNativeCreature.Ai.cs", "SelectPackage"),
    ];

    private enum Mode { Strings, ConditionFunctions, Procedures }
    private sealed record Callable(SyntaxNode Node, string Type, IReadOnlyList<string> Parameters, SyntaxNode Body, string File, SyntaxTree Tree);
    private sealed record Corpus(IReadOnlyDictionary<string, (SyntaxTree Tree, byte[] Bytes)> Files,
        ILookup<string, Callable> Methods, IReadOnlySet<string> Types, ILookup<string, (VariableDeclaratorSyntax Field, string Type)> Fields);

    private static readonly Dictionary<string, Corpus> Loaded = new(StringComparer.Ordinal);

    internal static Index Strings(string repository, IEnumerable<Root> roots) => Read(repository, roots, Mode.Strings);
    internal static Index ConditionFunctions(string repository) => Read(repository, Conditions, Mode.ConditionFunctions);
    internal static Index Procedures(string repository) => Read(repository, Packages, Mode.Procedures);

    private static Corpus Load(string repository)
    {
        var root = Path.GetFullPath(repository);
        if (Loaded.TryGetValue(root, out var cached)) return cached;
        var source = Path.Combine(root, "runtime", "src");
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Runtime dispatch source is absent: " + source);
        var files = new Dictionary<string, (SyntaxTree, byte[])>(StringComparer.Ordinal);
        var methods = new List<(string Name, Callable Callable)>();
        var fields = new List<(string Name, (VariableDeclaratorSyntax, string))>();
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            var bytes = File.ReadAllBytes(path);
            var tree = CSharpSyntaxTree.ParseText(System.Text.Encoding.UTF8.GetString(bytes), path: relative);
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException("Runtime source does not parse: " + relative);
            files.Add(relative, (tree, bytes));
            foreach (var type in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>()) types.Add(type.Identifier.ValueText);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                var type = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "";
                if (node is MethodDeclarationSyntax method && ((SyntaxNode?)method.Body ?? method.ExpressionBody?.Expression) is { } body)
                    methods.Add((method.Identifier.ValueText, new(method, type,
                        method.ParameterList.Parameters.Select(p => p.Identifier.ValueText).ToArray(), body, relative, tree)));
                else if (node is LocalFunctionStatementSyntax local && ((SyntaxNode?)local.Body ?? local.ExpressionBody?.Expression) is { } localBody)
                    methods.Add((local.Identifier.ValueText, new(local, type,
                        local.ParameterList.Parameters.Select(p => p.Identifier.ValueText).ToArray(), localBody, relative, tree)));
                else if (node is FieldDeclarationSyntax field)
                    foreach (var variable in field.Declaration.Variables) fields.Add((variable.Identifier.ValueText, (variable, type)));
            }
        }
        var corpus = new Corpus(files, methods.ToLookup(value => value.Name, value => value.Callable, StringComparer.Ordinal), types,
            fields.ToLookup(value => value.Name, value => value.Item2, StringComparer.Ordinal));
        Loaded.Add(root, corpus);
        return corpus;
    }

    private static Index Read(string repository, IEnumerable<Root> roots, Mode mode)
    {
        var corpus = Load(repository);
        var names = new Dictionary<string, List<Site>>(StringComparer.OrdinalIgnoreCase);
        var dynamic = new List<Site>();
        var used = new SortedSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            var matches = corpus.Methods[root.Method].Where(value => value.File == root.File).ToArray();
            if (matches.Length != 1) throw new InvalidDataException($"Expected one dispatch root {root.Method} in {root.File}.");
            var before = names.Values.Sum(sites => sites.Count);
            Visit(matches[0], root.Subjects.ToHashSet(StringComparer.Ordinal), root.Owner);
            // Fail closed: a renamed or restructured root must not read as "nothing supported".
            if (names.Values.Sum(sites => sites.Count) == before)
                throw new InvalidDataException($"Dispatch root {root.Method} in {root.File} yielded no guards.");
        }
        var literalDeferrals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (mode == Mode.Strings)
            foreach (var (_, (tree, _)) in corpus.Files)
                foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Where(IsDeferral))
                    if (call.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                        literalDeferrals.Add(literal.Token.ValueText);
        return new(names, used.Select(file => new Input(file, Convert.ToHexString(SHA256.HashData(corpus.Files[file].Bytes)))).ToArray(),
            dynamic, literalDeferrals);

        void Visit(Callable callable, HashSet<string> subjects, string owner)
        {
            if (!visited.Add($"{callable.File}:{callable.Node.SpanStart}:{string.Join(',', subjects.Order())}")) return;
            used.Add(callable.File);
            foreach (var alias in callable.Body.DescendantNodesAndSelf().OfType<VariableDeclaratorSyntax>())
                if (alias.Initializer is { } initializer && IsSubject(initializer.Value, subjects)) subjects.Add(alias.Identifier.ValueText);
            Site At(SyntaxNode node, string guard, string kind, SyntaxNode? branch) => new(owner, callable.File,
                callable.Tree.GetLineSpan(node.Span).StartLinePosition.Line + 1, Compact(guard), kind, branch is not null && Defers(branch), branch is not null && Rejects(branch));
            void Add(string? constant, Site site)
            {
                if (constant is null) return;
                var key = mode == Mode.Strings ? constant.ToLowerInvariant()[(constant.LastIndexOf('.') + 1)..] : constant;
                if (key.Length == 0) return;
                if (!names.TryGetValue(key, out var sites)) names.Add(key, sites = []);
                sites.Add(site);
            }
            foreach (var node in callable.Body.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.EqualsExpression):
                        if (IsSubject(binary.Left, subjects)) Add(Constant(binary.Right), At(binary, binary.ToString(), "equality", Branch(binary)));
                        else if (IsSubject(binary.Right, subjects)) Add(Constant(binary.Left), At(binary, binary.ToString(), "equality", Branch(binary)));
                        break;
                    // `if (operation != "x") return false;` admits x on the fallthrough path.
                    case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.NotEqualsExpression):
                        if (IsSubject(binary.Left, subjects)) Add(Constant(binary.Right), At(binary, binary.ToString(), "inequality-exit", null));
                        else if (IsSubject(binary.Right, subjects)) Add(Constant(binary.Left), At(binary, binary.ToString(), "inequality-exit", null));
                        break;
                    case IsPatternExpressionSyntax pattern when IsSubject(pattern.Expression, subjects):
                        foreach (var constant in Constants(pattern.Pattern)) Add(constant, At(pattern, pattern.ToString(), "pattern", Branch(pattern)));
                        break;
                    case SwitchStatementSyntax statement when IsSubject(statement.Expression, subjects):
                        foreach (var section in statement.Sections)
                            foreach (var label in section.Labels)
                                foreach (var constant in label switch
                                {
                                    CaseSwitchLabelSyntax simple => [Constant(simple.Value)],
                                    CasePatternSwitchLabelSyntax patterned => Constants(patterned.Pattern),
                                    _ => [],
                                })
                                    Add(constant, At(label, label.ToString(), "switch", section));
                        break;
                    case SwitchExpressionSyntax expression when IsSubject(expression.GoverningExpression, subjects):
                        foreach (var arm in expression.Arms)
                            foreach (var constant in Constants(arm.Pattern)) Add(constant, At(arm, arm.Pattern.ToString(), "switch-arm", arm.Expression));
                        break;
                    case RecursivePatternSyntax recursive when mode == Mode.Procedures && recursive.PropertyPatternClause is { } properties:
                        foreach (var property in properties.Subpatterns.Where(value => value.NameColon?.Name.Identifier.ValueText == "Procedure"))
                            foreach (var constant in Constants(property.Pattern)) Add(constant, At(recursive, recursive.ToString(), "property-pattern", null));
                        break;
                    case InvocationExpressionSyntax call when mode == Mode.Strings && !IsDeferral(call):
                        Invocation(call);
                        break;
                }
            }

            void Invocation(InvocationExpressionSyntax call)
            {
                var arguments = call.ArgumentList.Arguments;
                var member = call.Expression as MemberAccessExpressionSyntax;
                var name = member?.Name.Identifier.ValueText ?? (call.Expression as IdentifierNameSyntax)?.Identifier.ValueText;
                if (name is null) return;
                if (member is not null && name == "Equals" && IsSubject(member.Expression, subjects) && arguments.Count > 0)
                { Add(Constant(arguments[0].Expression), At(call, call.ToString(), "equals", Branch(call))); return; }
                if (member is not null && name is "Contains" or "ContainsKey" or "TryGetValue" && arguments.Count > 0 &&
                    IsSubject(arguments[0].Expression, subjects))
                {
                    var registry = member.Expression is MemberAccessExpressionSyntax qualified ? qualified.Name.Identifier.ValueText :
                        (member.Expression as IdentifierNameSyntax)?.Identifier.ValueText;
                    var declarations = registry is null ? [] : corpus.Fields[registry].ToArray();
                    var field = declarations.Where(value => value.Type == callable.Type).ToArray() is { Length: 1 } own ? own : declarations;
                    var keys = field.Length == 1 && field[0].Field.Initializer is { } initializer ? RegistryKeys(initializer.Value).ToArray() : [];
                    if (keys.Length == 0) dynamic.Add(At(call, call.ToString(), "unresolved-registry", null));
                    foreach (var key in keys) Add(key, At(call, call.ToString(), "registry:" + registry, Branch(call)));
                    return;
                }
                for (var position = 0; position < arguments.Count; ++position)
                {
                    if (!IsSubject(arguments[position].Expression, subjects)) continue;
                    var candidates = Resolve(call, name, member);
                    if (candidates.Length == 0) { dynamic.Add(At(call, call.ToString(), "unresolved-call", null)); continue; }
                    foreach (var candidate in candidates)
                    {
                        var parameter = arguments[position].NameColon?.Name.Identifier.ValueText ??
                            (position < candidate.Parameters.Count ? candidate.Parameters[position] : null);
                        if (parameter is not null && candidate.Parameters.Contains(parameter))
                            Visit(candidate, new HashSet<string>([parameter], StringComparer.Ordinal), owner);
                    }
                }
            }

            Callable[] Resolve(InvocationExpressionSyntax call, string name, MemberAccessExpressionSyntax? member)
            {
                var all = corpus.Methods[name].Where(value => value.Parameters.Count >= call.ArgumentList.Arguments.Count).ToArray();
                if (member is null)
                {
                    // A local function in the enclosing callable wins over a type member.
                    var local = all.Where(value => value.Node is LocalFunctionStatementSyntax && value.File == callable.File &&
                        callable.Node.Span.Contains(value.Node.Span)).ToArray();
                    return local.Length > 0 ? local : all.Where(value => value.Type == callable.Type && value.Node is MethodDeclarationSyntax).ToArray();
                }
                if (member.Expression is IdentifierNameSyntax receiver && corpus.Types.Contains(receiver.Identifier.ValueText))
                    return all.Where(value => value.Type == receiver.Identifier.ValueText && value.Node is MethodDeclarationSyntax).ToArray();
                if (member.Expression is ThisExpressionSyntax)
                    return all.Where(value => value.Type == callable.Type && value.Node is MethodDeclarationSyntax).ToArray();
                var methods = all.Where(value => value.Node is MethodDeclarationSyntax).ToArray();
                return methods.Select(value => value.Type).Distinct().Count() == 1 ? methods : [];
            }
        }

        bool IsSubject(ExpressionSyntax expression, IReadOnlySet<string> subjects)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized) expression = parenthesized.Expression;
            return mode switch
            {
                Mode.Strings => subjects.Contains(expression.ToString()) || expression is InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ToLowerInvariant" or "ToLower" } lowered
                } && IsSubject(lowered.Expression, subjects) || expression is InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Operation" } canonical,
                    ArgumentList.Arguments: [var argument]
                } && canonical.Expression.ToString().EndsWith("FalloutScriptCommandNames", StringComparison.Ordinal) &&
                    IsSubject(argument.Expression, subjects),
                Mode.Procedures => expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Procedure" },
                _ => true,
            };
        }

        string? Constant(ExpressionSyntax expression) => mode switch
        {
            Mode.Strings => expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
                ? literal.Token.ValueText : null,
            Mode.Procedures => expression is LiteralExpressionSyntax number && number.IsKind(SyntaxKind.NumericLiteralExpression)
                ? number.Token.ValueText : null,
            _ => expression is MemberAccessExpressionSyntax access &&
                access.Expression.ToString() is "Function" or "FalloutConditionFunction" or "Definitions.FalloutConditionFunction"
                ? access.Name.Identifier.ValueText : null,
        };

        // A negated pattern excludes its constants from dispatch.
        IEnumerable<string?> Constants(PatternSyntax pattern) => pattern.DescendantNodesAndSelf(node => node is not UnaryPatternSyntax)
            .OfType<ConstantPatternSyntax>().Select(value => Constant(value.Expression));

        IEnumerable<string> RegistryKeys(ExpressionSyntax initializer)
        {
            IEnumerable<ExpressionSyntax> elements = initializer switch
            {
                CollectionExpressionSyntax collection => collection.Elements.OfType<ExpressionElementSyntax>().Select(value => value.Expression),
                BaseObjectCreationExpressionSyntax { Initializer: { } body } => body.Expressions.Select(value => value switch
                {
                    AssignmentExpressionSyntax { Left: ImplicitElementAccessSyntax access } => access.ArgumentList.Arguments[0].Expression,
                    InitializerExpressionSyntax complex => complex.Expressions[0],
                    _ => value,
                }),
                _ => [],
            };
            foreach (var element in elements)
                if (Constant(element) is { } key) yield return key;
        }
    }

    // The statement a guard selects: an if/conditional's true arm. A guard
    // inside an expression-bodied predicate selects no branch here.
    private static SyntaxNode? Branch(SyntaxNode guard)
    {
        foreach (var ancestor in guard.Ancestors())
        {
            if (ancestor is IfStatementSyntax conditional && conditional.Condition.Span.Contains(guard.Span)) return conditional.Statement;
            if (ancestor is ConditionalExpressionSyntax ternary && ternary.Condition.Span.Contains(guard.Span)) return ternary.WhenTrue;
            if (ancestor is StatementSyntax or MemberDeclarationSyntax) return null;
        }
        return null;
    }

    private static bool IsDeferral(InvocationExpressionSyntax call) =>
        call.Expression.ToString().EndsWith("FalloutScriptCommandDeferral.Warn", StringComparison.Ordinal) && call.ArgumentList.Arguments.Count == 2;

    private static bool Defers(SyntaxNode branch) => branch.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
        .Any(call => IsDeferral(call) && call.ArgumentList.Arguments[1].Expression is not LiteralExpressionSyntax { Token.ValueText: HeadlessDeferral });

    private static bool Rejects(SyntaxNode branch)
    {
        var first = branch is BlockSyntax block ? block.Statements.FirstOrDefault() : branch;
        if (first is SwitchSectionSyntax section) first = section.Statements.FirstOrDefault();
        var thrown = first switch
        {
            ThrowStatementSyntax statement => statement.Expression,
            ThrowExpressionSyntax expression => expression.Expression,
            _ => null,
        };
        return thrown is ObjectCreationExpressionSyntax { Type: var type } && type.ToString().EndsWith("NotSupportedException", StringComparison.Ordinal);
    }

    private static string Compact(string text)
    {
        var single = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return single.Length <= 160 ? single : single[..157] + "...";
    }
}
