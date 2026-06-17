using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Wrapper
{
    public static class ParserExecutor
    {
        // Usa diretamente os parsers locais: LeanAST (AstBuilder) e RocqAST (RocqAstBuilder)
        public static Task<UnifiedASTNode> ExecuteRocqParserAsync(string filePath)
        {
            // RocqAstBuilder.Build(filePath) deve retornar uma coleção de comandos/objetos com
            // propriedades: Type, Command, Metadata e Children
            var cmds = RocqAstBuilder.Build(filePath);

            var root = new UnifiedASTNode
            {
                NodeType = NodeType.Other,
                Name = Path.GetFileName(filePath),
                Language = "Rocq",
                Metadata = new Dictionary<string, object> { ["source"] = filePath }
            };

            foreach (var c in cmds)
            {
                var cmdText = c.Command as string ?? string.Empty;
                var child = new UnifiedASTNode
                {
                    NodeType = MapStringToNodeType(c.Type as string),
                    Name = cmdText.Length > 80 ? cmdText.Substring(0, 80) : cmdText,
                    Language = "Rocq",
                    Metadata = c.Metadata as IDictionary<string, object> ?? new Dictionary<string, object>()
                };

                if (c.Children != null)
                {
                    foreach (var ch in c.Children)
                        child.Children.Add(new UnifiedASTNode { NodeType = NodeType.Other, Name = ch as string, Language = "Rocq" });
                }

                root.Children.Add(child);
            }

            return Task.FromResult(root);
        }

        public static Task<UnifiedASTNode> ExecuteLeanParserAsync(string filePath)
        {
            // AstBuilder.Build(filePath) retorna a raiz do AST do Lean
            var leanRoot = AstBuilder.Build(filePath);

            UnifiedASTNode Convert(dynamic node)
            {
                var u = new UnifiedASTNode
                {
                    NodeType = MapStringToNodeType((string)node.Kind),
                    Name = node.Value ?? Path.GetFileName(filePath),
                    Language = "Lean",
                    Metadata = node.ToDict() as IDictionary<string, object> ?? new Dictionary<string, object>()
                };

                if (node.Children != null)
                {
                    foreach (var child in node.Children)
                        u.Children.Add(Convert(child));
                }

                return u;
            }

            var root = Convert(leanRoot);
            root.Language = "Lean";
            return Task.FromResult(root);
        }

        private static NodeType MapStringToNodeType(string s)
        {
            if (string.IsNullOrEmpty(s)) return NodeType.Other;
            s = s.ToLowerInvariant();
            if (s.Contains("theorem")) return NodeType.Theorem;
            if (s.Contains("lemma")) return NodeType.Lemma;
            if (s.Contains("def") || s.Contains("definition")) return NodeType.Definition;
            if (s.Contains("fixpoint") || s.Contains("fix")) return NodeType.Fixpoint;
            if (s.Contains("inductive")) return NodeType.Inductive;
            return NodeType.Other;
        }
    }
}
