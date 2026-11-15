using System.Security.Cryptography;
using System.Text.Json;
using Xenon.Compiler;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Text;

// A public-API corpus runnable unchanged against preserved compiler binaries.
// Optimization-only differential coverage also lives in the normal compiler tests.
const string prelude = "namespace Corpus; struct Item { public int Value; } extern void Observe(readonly int& value); ";
var cases = new (string name, string source, string[] expected)[]
{
    ("valid-alias", "int Test(){int*p=new int();int*q=p;*q=42;int v=*p;delete(p);return v;}", []),
    ("use-after-free", "int Test(){int*p=new int();int*q=p;delete(p);return *q;}", ["XE2291"]),
    ("conditional-use", "void End(int*p,bool c){if(c)delete(p);}int Test(){int*p=new int();End(p,true);return *p;}", ["XE2291"]),
    ("conditional-valid", "void End(int*p,bool c){if(c)delete(p);}int Test(){int*p=new int();End(p,false);return *p;}", []),
    ("double-free", "void Test(){int*p=new int();delete(p);delete(p);}", ["XE2292"]),
    ("placement", "int Test(){Item*p=new Item();destruct(*p);*p=Item();return p->Value;}", []),
    ("checked-borrow", "void Write(int*p){*p=42;}void Test(){int*p=new int();readonly int&r=*p;Write(p);Observe(r);}", ["XE2232"]),
    ("independent-borrow", "void Write(int*p){*p=42;}void Test(){int*p=new int();int*q=new int();readonly int&r=*p;Write(q);Observe(r);}", []),
    ("ownership-transfer", "void Take(unique<int> p){} void Test(){unique<int>p=new int();Take(move p);Take(move p);}", ["XE2215"]),
    ("multiple-targets", "void End(int*p,int*q,bool c){int*a;if(c)a=p;else a=q;delete(a);}int Test(bool c){int*p=new int();int*q=new int();End(p,q,c);return *p;}", ["XE2291"]),
    ("recursive", "void A(int*p,bool c){if(c)B(p,false);else delete(p);}void B(int*p,bool c){A(p,c);}int Test(){int*p=new int();B(p,true);return *p;}", ["XE2291"]),
    ("unknown-raw", "extern int* Get();extern void Unknown(int*p);int Test(){int*p=Get();Unknown(p);return *p;}", []),
};
var output = new List<object>();
bool failed = false;
foreach (var (name, source, expected) in cases)
    Check(name, Compilation.Create(SourceText.From(prelude + source, name + ".xe")), expected);
var library = Compilation.Create(SourceText.From("namespace Lib; public void End(int*p){delete(p);}", "lib.xe"));
if (library.HasErrors) throw new Exception(string.Join('\n', library.Diagnostics));
var imported = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("LifetimeCorpus")), metadataOnly: true);
Check("imported-free", Compilation.Create(new CompilationOptions(), [imported], SourceText.From(
    "using Lib; namespace Client; int Test(){int*p=new int();End(p);return *p;}", "client.xe")), ["XE2291"]);
Console.WriteLine(JsonSerializer.Serialize(new {
    compilerSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Compilation).Assembly.Location))),
    passed = !failed, cases = output
}, new JsonSerializerOptions { WriteIndented = true }));
return failed ? 1 : 0;

void Check(string name, Compilation compilation, string[] expected)
{
    var diagnostics = compilation.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
        .Select(d => new { d.Id, start = d.Location.Span.Start }).OrderBy(d => d.start).ThenBy(d => d.Id).ToArray();
    var actual = diagnostics.Select(d => d.Id).Distinct().Order().ToArray();
    bool passed = actual.SequenceEqual(expected.Order());
    failed |= !passed;
    output.Add(new { name, passed, expected, diagnostics });
}
