using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

var path = args[0];
var needles = args.Skip(1).ToArray();
using var fs = File.OpenRead(path);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();
string Name(StringHandle h) => md.GetString(h);
if (needles.Length > 0 && needles[0] == "--strings")
{
    // dump.dll <assembly> --strings [regex]: print the user-string literals (#US heap), optionally filtered
    var rx = needles.Length > 1 ? new System.Text.RegularExpressions.Regex(needles[1]) : null;
    var h = MetadataTokens.UserStringHandle(1);
    while (!h.IsNil)
    {
        var s = md.GetUserString(h);
        if (rx == null || rx.IsMatch(s)) Console.WriteLine(s.Replace("\n", "\\n"));
        h = md.GetNextHandle(h);
    }
    return;
}
if (needles.Length > 1 && needles[0] == "--grep")
{
    // dump.dll <assembly> --grep <regex>: one line per method or field whose name matches, "Type::member(sig)"
    var rx = new System.Text.RegularExpressions.Regex(needles[1]);
    foreach (var th in md.TypeDefinitions)
    {
        var t = md.GetTypeDefinition(th);
        var full = (Name(t.Namespace).Length > 0 ? Name(t.Namespace) + "." : "") + Name(t.Name);
        foreach (var mh in t.GetMethods())
        {
            var m = md.GetMethodDefinition(mh);
            var n = Name(m.Name);
            if (n.StartsWith("NativeMethodInfoPtr") || !rx.IsMatch(n)) continue;
            var dec = m.DecodeSignature(new SigProv(md), null);
            Console.WriteLine(full + "::" + ((m.Attributes & System.Reflection.MethodAttributes.Static) != 0 ? "static " : "") + dec.ReturnType + " " + n + "(" + string.Join(", ", dec.ParameterTypes) + ")");
        }
        foreach (var fh in t.GetFields())
        {
            var f = md.GetFieldDefinition(fh);
            var n = Name(f.Name);
            if (n.StartsWith("NativeFieldInfoPtr") || !rx.IsMatch(n)) continue;
            Console.WriteLine(full + "::field " + f.DecodeSignature(new SigProv(md), null) + " " + n);
        }
    }
    return;
}
bool memberMode = needles.Length > 1 && needles[0] == "--member";
if (memberMode) needles = needles.Skip(1).ToArray();
foreach (var th in md.TypeDefinitions)
{
    var t = md.GetTypeDefinition(th);
    var full = (Name(t.Namespace).Length > 0 ? Name(t.Namespace) + "." : "") + Name(t.Name);
    if (memberMode)
    {
        // dump.dll <assembly> --member <name>...: print the types that declare a method or field with that exact name,
        // then dump them like a normal type query
        bool has = t.GetMethods().Any(mh => needles.Contains(Name(md.GetMethodDefinition(mh).Name)))
                || t.GetFields().Any(fh => needles.Contains(Name(md.GetFieldDefinition(fh).Name)));
        if (!has) continue;
    }
    else if (!needles.Any(n => full.EndsWith(n) || full.Contains("." + n))) continue;
    Console.WriteLine("== " + full + "  : " + (t.BaseType.IsNil ? "" : BaseName(md, t.BaseType)));
    foreach (var mh in t.GetMethods())
    {
        var m = md.GetMethodDefinition(mh);
        var n = Name(m.Name);
        if (n.StartsWith("NativeMethodInfoPtr")) continue;
        var dec = m.DecodeSignature(new SigProv(md), null);
        Console.WriteLine("   " + ((m.Attributes & System.Reflection.MethodAttributes.Static) != 0 ? "static " : "") + dec.ReturnType + " " + n + "(" + string.Join(", ", dec.ParameterTypes) + ")");
    }
    foreach (var fh in t.GetFields())
    {
        var f = md.GetFieldDefinition(fh);
        var n = Name(f.Name);
        if (n.StartsWith("NativeFieldInfoPtr")) continue;
        Console.WriteLine("   field " + f.DecodeSignature(new SigProv(md), null) + " " + n);
    }
}
static string BaseName(MetadataReader md, EntityHandle h)
{
    if (h.Kind == HandleKind.TypeReference) { var r = md.GetTypeReference((TypeReferenceHandle)h); return md.GetString(r.Namespace) + "." + md.GetString(r.Name); }
    if (h.Kind == HandleKind.TypeDefinition) { var d = md.GetTypeDefinition((TypeDefinitionHandle)h); return md.GetString(d.Namespace) + "." + md.GetString(d.Name); }
    return h.Kind.ToString();
}
class SigProv : ISignatureTypeProvider<string, object>
{
    readonly MetadataReader md; public SigProv(MetadataReader m) { md = m; }
    public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString();
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) { var d = r.GetTypeDefinition(h); return r.GetString(d.Name); }
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) { var d = r.GetTypeReference(h); return r.GetString(d.Name); }
    public string GetTypeFromSpecification(MetadataReader r, object c, TypeSpecificationHandle h, byte k) => r.GetTypeSpecification(h).DecodeSignature(this, c);
    public string GetSZArrayType(string e) => e + "[]";
    public string GetArrayType(string e, ArrayShape s) => e + "[,]";
    public string GetByReferenceType(string e) => "ref " + e;
    public string GetPointerType(string e) => e + "*";
    public string GetGenericInstantiation(string g, System.Collections.Immutable.ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
    public string GetGenericMethodParameter(object c, int i) => "!!" + i;
    public string GetGenericTypeParameter(object c, int i) => "!" + i;
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetModifiedType(string m, string u, bool r) => u;
    public string GetPinnedType(string e) => e;
    public string GetTypeFromSerializedName(string n) => n;
}
