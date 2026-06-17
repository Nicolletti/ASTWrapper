using System;
using System.Collections.Generic;

namespace Wrapper
{
    public enum NodeType
    {
        Definition,
        Theorem,
        Lemma,
        Fixpoint,
        Inductive,
        Other
    }

    public class UnifiedASTNode
    {
        public NodeType NodeType { get; set; }
        public string Name { get; set; }
        public string Language { get; set; }
        public List<UnifiedASTNode> Children { get; set; } = new List<UnifiedASTNode>();
        public object Metadata { get; set; }
    }

    public class ProcessingResult
    {
        public bool Success { get; set; }
        public string FileName { get; set; }
        public string Language { get; set; }
        public UnifiedASTNode Ast { get; set; }
        public string Error { get; set; }
        public long ProcessingTime { get; set; }
    }
}
