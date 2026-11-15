using System.Diagnostics;
using System.Text.Json;
using System.Reflection;
using Xenon.Compiler;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Text;
const string prelude = "namespace Bench; struct Item { public int Value; public int Read(){return Value;} } ";
string Width(int n) => "void D(" + string.Join(',', Enumerable.Range(0,n).Select(i=>$"Item* p{i}")) + "){"+string.Join(' ',Enumerable.Range(0,n).Select(i=>$"delete(p{i});"))+"} int Test(){Item*p=new Item();D("+string.Join(',',Enumerable.Range(0,n).Select(i=>i==n-1?"p":"null"))+");return p->Read();}";
var chain=string.Join(' ',Enumerable.Range(0,70).Select(i=>$"void F{i}(Item*p){{"+(i==69?"delete(p);":$"F{i+1}(p);")+"}"))+"int Test(){Item*p=new Item();F0(p);return p->Read();}";
const string asyncSource="""
public struct Result {
public static void operator resolve(Result& v,int x){}
public static void operator reject(Result& v,int error){}
public static bool operator await(readonly Result& v,storage<int>& r,function void() next){r=42;return true;}}
public struct Operation { public static bool operator await(readonly Operation& v,storage<int>& r,function void() next){r=1;return true;} }
async Result Read(int*p){await Operation();return *p;}
Result Make(){int x=42;return Read(&x);}
""";
var cases=new Dictionary<string,string>{["frees10"]=Width(10),["frees50"]=Width(50),["frees130"]=Width(130),["frees250"]=Width(250),["frees500"]=Width(500),["frees1000"]=Width(1000),["chain70"]=chain,
["nested"]="void R(Item*p,bool a,bool b){destruct(*p);if(a){if(b)*p=Item();}}int Test(bool a,bool b){Item*p=new Item();R(p,a,b);if(!a)return p->Read();return 0;}",
["recursive"]="void A(Item*p,bool b){if(b)B(p,false);else delete(p);}void B(Item*p,bool b){A(p,b);}int Test(){Item*p=new Item();B(p,true);return p->Read();}",
["async"]=asyncSource,
["deep"]="struct S0 { public int V; } "+string.Join(' ',Enumerable.Range(1,16).Select(i=>$"struct S{i} {{ public S{i-1} N; }}"))+"int Test(){S16*p=new S16();int*q=&p->"+string.Concat(Enumerable.Repeat("N.",16))+"V;delete(p);return *q;}"};
var name=args[0];int repeats=args.Length>1?int.Parse(args[1]):3;
bool profile = args.Contains("--profile");
var events = new List<object>();
var observer = typeof(Compilation).Assembly.GetType("Xenon.Compiler.Mir.Analysis.MirAnalysisProfile")?.GetField("Observer",BindingFlags.Public|BindingFlags.Static);
var warmTimer=Stopwatch.StartNew();
_ = Compilation.Create(SourceText.From(prelude+cases[name],name+"-warm.xe")).HasErrors;
Console.Error.WriteLine(JsonSerializer.Serialize(new{stage="warmup",elapsedMs=warmTimer.Elapsed.TotalMilliseconds}));
var samples=new List<object>();
Compilation? last = null;
for(int i=0;i<repeats;i++){
 GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
 events.Clear();
 if(profile) observer?.SetValue(null,(Action<string,double,long,long,int,int>)((phase,ms,bytes,iterations,blocks,statements)=>events.Add(new{phase,ms,bytes,iterations,blocks,statements})));
 using var process=Process.GetCurrentProcess();
 var cpu=process.TotalProcessorTime;
 long allocated=GC.GetTotalAllocatedBytes(true);var timer=Stopwatch.StartNew();
 var compilation=Compilation.Create(SourceText.From(prelude+cases[name],name+".xe"));bool errors=compilation.HasErrors;
 timer.Stop();long bytes=GC.GetTotalAllocatedBytes(true)-allocated;observer?.SetValue(null,null);
 double cpuMs=(process.TotalProcessorTime-cpu).TotalMilliseconds;
 if(!errors)throw new Exception("Expected lifetime violation was missed: "+name);
 var ids=compilation.Diagnostics.Where(d=>d.Severity==Xenon.Compiler.Diagnostics.DiagnosticSeverity.Error).Select(d=>d.Id).Distinct().ToArray();
 if(ids.Any(id=>name=="async"?id is not("XE2284" or "XE2285"):id!="XE2291"))throw new Exception(string.Join("\n",compilation.Diagnostics));
 samples.Add(new{iteration=i,elapsedMs=timer.Elapsed.TotalMilliseconds,cpuMs,allocatedBytes=bytes,peakWorkingSetBytes=process.PeakWorkingSet64,ids,profile=events.ToArray()});
 Console.Error.WriteLine(JsonSerializer.Serialize(new{stage="sample",iteration=i,elapsedMs=timer.Elapsed.TotalMilliseconds,cpuMs,allocatedBytes=bytes,peakWorkingSetBytes=process.PeakWorkingSet64,ids}));
 last=compilation;
}
var bodies=last!.SemanticModel.Functions.Select(function=>MirLowerer.Lower(function,last.TypeFactory,last.SemanticModel.ExpressionLocations,diagnosticRecovery:true)).ToArray();
var shape=new{blocks=bodies.Sum(body=>body.Blocks.Length),statements=bodies.Sum(body=>body.Blocks.Sum(block=>block.Statements.Length)),destructionEffects=last.GetDeclaredFunctionSymbols().Sum(function=>function.MirLifetimeSummary?.Effects.Count(effect=>effect.InvalidatesBorrow)??0)};
Console.WriteLine(JsonSerializer.Serialize(new{scenario=name,samples,shape,scenarioWarmup=true,profileAvailable=observer is not null,environment=new{framework=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,os=System.Runtime.InteropServices.RuntimeInformation.OSDescription,processors=Environment.ProcessorCount}}));
