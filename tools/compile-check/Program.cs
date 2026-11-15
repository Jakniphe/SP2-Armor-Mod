using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// Compile check without the game's DLLs.
//
// Why not just build the mod? The mod needs the game's types, and the only copy of them here is the decompiled source,
// which has errors of its own (packages missing, decompiler artifacts). The C# compiler gives up on method bodies when
// declarations have errors anywhere. So this program asks Roslyn for the diagnostics of the MOD's files only, through
// the semantic model: the decompiled game is part of the compilation (its declarations are what the mod is checked
// against), but its errors are not reported.
//
//   CompileCheck --mod <mod folder> --src <decompiled folder>... --extra <stand-in folder>... --ref <dll or folder>...
//                [--subst <file>] [--show-ignored]
//
// A line of the --subst file, "old=>new", is replaced in the mod's text before it is read: for the few Unity 6 names that the
// Unity 2021 reference DLLs do not have (so the check does not report them).
internal static class Program
{
    private static int Main(string[] args)
    {
        string modDir = null;
        List<string> sources = new List<string>();
        List<string> extras = new List<string>();
        List<string> refs = new List<string>();
        string substFile = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--mod": modDir = args[++i]; break;
                case "--src": sources.Add(args[++i]); break;
                case "--extra": extras.Add(args[++i]); break;
                case "--ref": refs.Add(args[++i]); break;
                case "--subst": substFile = args[++i]; break;
            }
        }
        if (modDir == null) { Console.Error.WriteLine("--mod <folder> is required"); return 2; }
        modDir = Path.GetFullPath(modDir);

        List<KeyValuePair<string, string>> substitutions = new List<KeyValuePair<string, string>>();
        if (substFile != null && File.Exists(substFile))
        {
            foreach (string line in File.ReadAllLines(substFile))
            {
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int at = line.IndexOf("=>", StringComparison.Ordinal);
                if (at > 0) substitutions.Add(new KeyValuePair<string, string>(line.Substring(0, at), line.Substring(at + 2)));
            }
        }

        CSharpParseOptions parse = new CSharpParseOptions(LanguageVersion.Latest);
        List<SyntaxTree> trees = new List<SyntaxTree>();
        HashSet<SyntaxTree> modTrees = new HashSet<SyntaxTree>();

        foreach (string file in Directory.EnumerateFiles(modDir, "*.cs", SearchOption.AllDirectories))
        {
            string rel = file.Substring(modDir.Length).Replace('\\', '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
            string text = File.ReadAllText(file);
            foreach (KeyValuePair<string, string> pair in substitutions) text = text.Replace(pair.Key, pair.Value);
            SyntaxTree tree = CSharpSyntaxTree.ParseText(text, parse, file);
            trees.Add(tree);
            modTrees.Add(tree);
        }
        foreach (string dir in sources.Concat(extras))
            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file), parse, file));

        List<MetadataReference> references = new List<MetadataReference>();
        foreach (string r in refs)
        {
            if (Directory.Exists(r))
                foreach (string dll in Directory.EnumerateFiles(r, "*.dll")) references.Add(MetadataReference.CreateFromFile(dll));
            else if (File.Exists(r))
                references.Add(MetadataReference.CreateFromFile(r));
        }

        CSharpCompilation compilation = CSharpCompilation.Create(
            "ModCheck", trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                                         nullableContextOptions: NullableContextOptions.Disable));

        int errors = 0;
        foreach (SyntaxTree tree in modTrees.OrderBy(t => t.FilePath))
        {
            SemanticModel model = compilation.GetSemanticModel(tree);
            foreach (Diagnostic d in model.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
                                                                .OrderBy(d => d.Location.SourceSpan.Start))
            {
                FileLinePositionSpan pos = d.Location.GetLineSpan();
                string rel = tree.FilePath.Substring(modDir.Length + 1).Replace('\\', '/');
                Console.WriteLine(rel + "(" + (pos.StartLinePosition.Line + 1) + "," + (pos.StartLinePosition.Character + 1) +
                                  "): error " + d.Id + ": " + d.GetMessage());
                errors++;
            }
        }

        Console.WriteLine(errors == 0
            ? "COMPILE CHECK: no errors in the mod (" + modTrees.Count + " files)"
            : "COMPILE CHECK: " + errors + " error(s) in the mod (" + modTrees.Count + " files)");
        return errors == 0 ? 0 : 1;
    }
}
