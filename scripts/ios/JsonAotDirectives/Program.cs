// Generates NativeAOT rd.xml directives so reflection-based System.Text.Json
// can serialize OpenNV types on iOS. NativeAOT cannot create generic converter
// instantiations over value types at runtime; this pre-instantiates the
// converters, property/parameter infos and collection converters that the
// reflection resolver constructs for every type reachable from OpenNV.
// Usage: dotnet run -- <OpenNV.dll> <output rd.xml>
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;

var assemblyPath = Path.GetFullPath(args[0]);
var directory = Path.GetDirectoryName(assemblyPath)!;
AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
{
    var name = new AssemblyName(request.Name).Name + ".dll";
    var candidate = Path.Combine(directory, name);
    if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
    // Godot's managed bindings resolve from the NuGet package cache.
    var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    var cached = Directory.Exists(packages)
        ? Directory.EnumerateFiles(packages, name, SearchOption.AllDirectories).OrderByDescending(path => path).FirstOrDefault()
        : null;
    return cached is null ? null : Assembly.LoadFrom(cached);
};
var root = Assembly.LoadFrom(assemblyPath);
var stj = typeof(System.Text.Json.JsonSerializer).Assembly;
Type Stj(string name) => stj.GetType(name, throwOnError: true)!;
const string C = "System.Text.Json.Serialization.Converters.";
const string M = "System.Text.Json.Serialization.Metadata.";

var visited = new HashSet<Type>();
var pending = new Queue<Type>();
var directives = new SortedSet<string>(StringComparer.Ordinal);
void Visit(Type? type)
{
    if (type is null || type.IsGenericParameter || type.ContainsGenericParameters || type.IsPointer ||
        type.IsByRef || type.IsByRefLike || type == typeof(void) || typeof(Delegate).IsAssignableFrom(type) ||
        CompilerGenerated(type)) return;
    if (visited.Add(type)) pending.Enqueue(type);
}
void Emit(Type definition, params Type[] arguments)
{
    try { directives.Add(Name(definition.MakeGenericType(arguments))); }
    catch (ArgumentException) { /* constraint not satisfied: STJ never builds it either */ }
}
static string Name(Type type)
{
    if (!type.IsGenericType) return $"{type.FullName}, {type.Assembly.GetName().Name}";
    var definition = type.GetGenericTypeDefinition();
    var arguments = string.Join(",", type.GetGenericArguments().Select(argument => $"[{Name(argument)}]"));
    return $"{definition.FullName}[{arguments}], {definition.Assembly.GetName().Name}";
}

Type[] loadable;
try { loadable = root.GetTypes(); }
catch (ReflectionTypeLoadException error) { loadable = error.Types.Where(type => type is not null).ToArray()!; }
foreach (var type in loadable)
    if (!type.IsGenericTypeDefinition && !type.IsAbstract && !type.Name.Contains('<') &&
        !IsGodotObject(type)) Visit(type);

// Compiler-generated types (regex/LINQ helpers, closures) are never
// serialized and their mangled names cannot be resolved by directive.
static bool CompilerGenerated(Type type) =>
    (type.FullName ?? type.Name).Contains('<') || type.Namespace?.EndsWith(".Generated", StringComparison.Ordinal) == true ||
    type.IsGenericType && type.GetGenericArguments().Any(CompilerGenerated) ||
    type.HasElementType && CompilerGenerated(type.GetElementType()!);

static bool IsGodotObject(Type type)
{
    for (var current = type; current is not null; current = current.BaseType)
        if (current.FullName == "Godot.GodotObject") return true;
    return false;
}

while (pending.TryDequeue(out var type))
{
    // Every type may be a property, parameter or root value.
    Emit(Stj(M + "JsonTypeInfo`1"), type);
    Emit(Stj(M + "JsonPropertyInfo`1"), type);
    Emit(Stj(M + "JsonParameterInfo`1"), type);
    if (Nullable.GetUnderlyingType(type) is { } underlying) { Emit(Stj(C + "NullableConverter`1"), underlying); Visit(underlying); continue; }
    if (type.IsEnum) { Emit(Stj(C + "EnumConverter`1"), type); Visit(typeof(Nullable<>).MakeGenericType(type)); continue; }
    if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal)) continue;
    if (type.IsArray)
    {
        var element = type.GetElementType()!;
        if (type.GetArrayRank() == 1) Emit(Stj(C + "ArrayConverter`2"), type, element);
        Visit(element); continue;
    }
    if (Collection(type)) continue;
    if (type.IsValueType && !type.IsPrimitive) Visit(typeof(Nullable<>).MakeGenericType(type));
    if (type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true && type.Assembly != root) continue;

    Emit(Stj(C + "ObjectDefaultConverter`1"), type);
    var constructor = Constructor(type);
    if (constructor is not null)
    {
        var parameters = constructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        if (parameters.Length <= 4)
            Emit(Stj(C + "SmallObjectWithParameterizedConstructorConverter`5"),
                [type, .. parameters, .. Enumerable.Repeat(typeof(object), 4 - parameters.Length)]);
        else
        {
            Emit(Stj(C + "LargeObjectWithParameterizedConstructorConverter`1"), type);
            Emit(Stj(C + "LargeObjectWithParameterizedConstructorConverterWithReflection`1"), type);
        }
        foreach (var parameter in parameters) Visit(parameter);
    }
    foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        if (property.GetIndexParameters().Length == 0 && property.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            Visit(property.PropertyType);
}

bool Collection(Type type)
{
    if (!type.IsGenericType) return false;
    var definition = type.GetGenericTypeDefinition();
    var arguments = type.GetGenericArguments();
    string? converter = definition.FullName switch
    {
        "System.Collections.Generic.List`1" => "ListOfTConverter`2",
        "System.Collections.Generic.IList`1" => "IListOfTConverter`2",
        "System.Collections.Generic.ICollection`1" => "ICollectionOfTConverter`2",
        "System.Collections.Generic.IEnumerable`1" or "System.Collections.Generic.IReadOnlyList`1" or
            "System.Collections.Generic.IReadOnlyCollection`1" => "IEnumerableOfTConverter`2",
        "System.Collections.Generic.HashSet`1" or "System.Collections.Generic.ISet`1" or
            "System.Collections.Generic.IReadOnlySet`1" or "System.Collections.Generic.SortedSet`1" => "ISetOfTConverter`2",
        "System.Collections.Generic.Queue`1" => "QueueOfTConverter`2",
        "System.Collections.Generic.Stack`1" => "StackOfTConverter`2",
        "System.Collections.Immutable.ImmutableArray`1" or "System.Collections.Immutable.ImmutableList`1" or
            "System.Collections.Immutable.IImmutableList`1" or "System.Collections.Immutable.ImmutableHashSet`1" =>
            "ImmutableEnumerableOfTConverterWithReflection`2",
        "System.Collections.Generic.Dictionary`2" or "System.Collections.Generic.SortedDictionary`2" =>
            "DictionaryOfTKeyTValueConverter`3",
        "System.Collections.Generic.IDictionary`2" => "IDictionaryOfTKeyTValueConverter`3",
        "System.Collections.Generic.IReadOnlyDictionary`2" => "IReadOnlyDictionaryOfTKeyTValueConverter`3",
        "System.Collections.Immutable.ImmutableDictionary`2" or "System.Collections.Immutable.IImmutableDictionary`2" or
            "System.Collections.Immutable.ImmutableSortedDictionary`2" => "ImmutableDictionaryOfTKeyTValueConverterWithReflection`3",
        _ => null,
    };
    if (converter is null) return false;
    var stjType = Stj(C + converter);
    Emit(stjType, [type, .. arguments]);
    // Dictionaries keyed or valued by the same type also need their pair infos.
    foreach (var argument in arguments) Visit(argument);
    return true;
}

static ConstructorInfo? Constructor(Type type)
{
    var constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public);
    var marked = constructors.Where(c => c.GetCustomAttribute<JsonConstructorAttribute>() is not null).ToArray();
    if (marked.Length == 1) return marked[0].GetParameters().Length == 0 ? null : marked[0];
    if (type.IsValueType || constructors.Any(c => c.GetParameters().Length == 0)) return null;
    return constructors.Length == 1 && constructors[0].GetParameters().Length > 0 ? constructors[0] : null;
}

var xml = new StringBuilder();
xml.AppendLine("<!-- Generated by scripts/ios/JsonAotDirectives; do not edit. -->");
xml.AppendLine("<Directives xmlns=\"http://schemas.microsoft.com/netfx/2013/01/metadata\">");
xml.AppendLine("  <Application>");
foreach (var group in directives.GroupBy(d => d[(d.LastIndexOf(", ", StringComparison.Ordinal) + 2)..]))
{
    xml.AppendLine($"    <Assembly Name=\"{group.Key}\">");
    foreach (var directive in group)
        xml.AppendLine($"      <Type Name=\"{System.Security.SecurityElement.Escape(directive[..directive.LastIndexOf(", ", StringComparison.Ordinal)])}\" Dynamic=\"Required All\" />");
    xml.AppendLine("    </Assembly>");
}
xml.AppendLine("  </Application>");
xml.AppendLine("</Directives>");
File.WriteAllText(args[1], xml.ToString());
Console.WriteLine($"types={visited.Count} directives={directives.Count} -> {args[1]}");
