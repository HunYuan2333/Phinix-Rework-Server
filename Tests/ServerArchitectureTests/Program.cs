using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class Program
{
    private sealed record Finding(string Path, string Method, string Identifier);
    private static readonly HashSet<string> DomainForbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "FrameworkPacket", "ServerFrameworkContext", "FrameworkTradeProtocol", "AuthenticatePacket",
        "AuthTypes", "ClientKey", "Username", "Password", "Token", "ProtocolMode", "ProtocolVersion", "ProtocolFamily", "WireProfile", "IsLegacy", "V2"
    };
    private static bool IsForbidden(string identifier, bool domain) =>
        identifier.Contains("Legacy", StringComparison.OrdinalIgnoreCase) ||
        identifier.Contains("Fallback", StringComparison.OrdinalIgnoreCase) ||
        (domain && DomainForbidden.Contains(identifier)) ||
        (!domain && (identifier.StartsWith("ChatExtension", StringComparison.Ordinal) ||
                     identifier.StartsWith("TradeExtension", StringComparison.Ordinal) ||
                     identifier is "IFrameworkTradeServerApi" or "IFrameworkChatServerApi" or "PhinixFrameworkTradeServerService" or "PhinixFrameworkChatService"));

    private static List<Finding> Inspect(string path, string source, bool domain)
    {
        var syntax = CSharpSyntaxTree.ParseText(source);
        if (syntax.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new InvalidOperationException("Syntax parser failed for " + path);
        var result = new List<Finding>();
        // Syntax tokens exclude comments/string contents. No regex-only forbidden-word oracle.
        foreach (var token in syntax.GetRoot().DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)))
        {
            if (!IsForbidden(token.ValueText, domain)) continue;
            string method = token.Parent.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "<type>";
            result.Add(new Finding(path, method, token.ValueText));
        }
        if (domain)
            foreach (var condition in syntax.GetRoot().DescendantNodes().OfType<IfStatementSyntax>().Select(node => node.Condition))
                foreach (var token in condition.DescendantTokens().Where(token => token.IsKind(SyntaxKind.StringLiteralToken)))
                    if (new[] { "Legacy", "V2", "Original", "ExistingRework" }.Contains(token.ValueText, StringComparer.OrdinalIgnoreCase))
                    {
                        string method = condition.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "<type>";
                        result.Add(new Finding(path, method, "protocol-literal:" + token.ValueText));
                    }
        return result;
    }

    private static bool ContainsCoreDeclaration(string source) => CSharpSyntaxTree.ParseText(source).GetRoot()
        .DescendantNodes().OfType<TypeDeclarationSyntax>().Any(type => type.Identifier.ValueText == "PhinixFrameworkTradeStore");

    private static int Main(string[] args)
    {
        try
        {
            string root = args.Length == 1 ? Path.GetFullPath(args[0]) : FindRoot();
            string scopeFile = Path.Combine(root, "Tests/ServerArchitectureTests/architecture-scope.json");
            using var scope = JsonDocument.Parse(File.ReadAllText(scopeFile));
            var findings = new List<Finding>();
            var classified = new HashSet<string>(StringComparer.Ordinal);
            foreach (string kind in new[] { "domain", "host" })
                foreach (var path in scope.RootElement.GetProperty(kind).EnumerateArray())
                {
                    classified.Add(path.GetString());
                    findings.AddRange(Inspect(path.GetString(), File.ReadAllText(Path.Combine(root, path.GetString())), kind == "domain"));
                }
            foreach (string kind in new[] { "nonDomainFiles", "transitionalBoundaries" })
                foreach (var entry in scope.RootElement.GetProperty(kind).EnumerateArray())
                    classified.Add(entry.GetProperty("path").GetString());
            foreach (var directory in scope.RootElement.GetProperty("classifiedDirectories").EnumerateArray())
                foreach (string file in Directory.EnumerateFiles(Path.Combine(root, directory.GetString()), "*.cs", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (relative.Split('/').Any(part => part is "bin" or "obj")) continue;
                    if (!classified.Contains(relative)) throw new InvalidOperationException("Review architecture role for new source: " + relative);
                }
            var allowed = scope.RootElement.GetProperty("baseline").EnumerateArray().ToDictionary(
                item => new Finding(item.GetProperty("path").GetString(), item.GetProperty("method").GetString(), item.GetProperty("identifier").GetString()),
                item => item.GetProperty("count").GetInt32());
            var actual = findings.GroupBy(f => f).ToDictionary(g => g.Key, g => g.Count());
            bool baselineMatches = actual.Count == allowed.Count && actual.All(pair => allowed.TryGetValue(pair.Key, out int count) && count == pair.Value);
            if (!baselineMatches) throw new InvalidOperationException("Architecture violation set differs from explicit debt baseline: " + JsonSerializer.Serialize(actual.Select(x => new { finding = x.Key, count = x.Value })));
            Console.WriteLine(JsonSerializer.Serialize(new { id = "TQ01-syntax-baseline", passed = true, remainingDebt = findings.Count, findings }));

            foreach (var rule in scope.RootElement.GetProperty("assemblyRules").EnumerateObject())
            {
                using var stream = File.OpenRead(Path.Combine(root, rule.Name));
                using var pe = new PEReader(stream);
                var metadata = pe.GetMetadataReader();
                string[] references = metadata.AssemblyReferences.Select(h => metadata.GetString(metadata.GetAssemblyReference(h).Name)).ToArray();
                string[] forbidden = rule.Value.EnumerateArray().Select(e => e.GetString()).ToArray();
                if (references.Intersect(forbidden).Any()) throw new InvalidOperationException("Forbidden assembly dependency in " + rule.Name);
                Console.WriteLine(JsonSerializer.Serialize(new { id = "TQ01-assembly-rule", assembly = rule.Name, passed = true, references }));
            }
            string receiptAdapter = File.ReadAllText(Path.Combine(root,"Extensions/Trade/Server/TradeDeliveryReceipts.cs"));
            if (ContainsCoreDeclaration(receiptAdapter) || !ContainsCoreDeclaration("partial class PhinixFrameworkTradeStore {}") ||
                ContainsCoreDeclaration("// PhinixFrameworkTradeStore\nclass ReceiptAdapter {}"))
                throw new InvalidOperationException("Receipt wire adapter must not contain core store declarations");
            Console.WriteLine("TQ01 receipt adapter/core declaration separation passed");
            // Negative controls exercise syntax and domain/host policies, not text matching.
            if (Inspect("negative", "class X { void Go() { if (isLegacy) { FrameworkPacket p = null; } } }", true).Count != 2 ||
                Inspect("negative", "class X { void Go() { IFrameworkTradeServerApi api = null; } }", false).Count != 1 ||
                Inspect("negative", "class X { void Go(string family) { if (family == \"Legacy\") {} } }", true).Count != 1 ||
                Inspect("comment", "// LegacyFallback FrameworkPacket\nclass X { string note = \"LegacyFallback FrameworkPacket\"; }", true).Count != 0)
                throw new InvalidOperationException("Architecture negative control failed");
            Console.WriteLine($"ARCHITECTURE RESULT passed; baselineDebt={findings.Count}; negativeControls=5");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Server/Server.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found");
    }
}
