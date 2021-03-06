﻿using System.Collections.Generic;

namespace ProtoCore.DSASM
{
    /// <summary>
    /// InstructionStream holds the executable dsasm code and relevant information
    /// </summary>
    /// 
    public class InstructionStream
    {
        public ProtoCore.Language language { get; set; }
        public int entrypoint { get; set; }
        public List<Instruction> instrList { get; set; }
        public ProtoCore.AssociativeGraph.DependencyGraph dependencyGraph { get; set; }
        public List<ProtoCore.AssociativeGraph.UpdateNodeRef> xUpdateList { get; set; }

        public InstructionStream(ProtoCore.Language langId, ProtoLanguage.CompileStateTracker compileState)
        {
            language = langId;
            entrypoint = Constants.kInvalidIndex;
            instrList = new List<Instruction>();
            dependencyGraph = new ProtoCore.AssociativeGraph.DependencyGraph(compileState);
            xUpdateList = new List<AssociativeGraph.UpdateNodeRef>();
        }
    }

    /// <summary>
    /// Executable holds the body of code that will be executed along with associated
    /// meta-information
    /// </summary>
    /// 
    public class Executable
    {
        public bool isSingleAssocBlock { get; set; }


        public ProtoCore.DSASM.ClassTable classTable { get; set; }
        public ProtoCore.DSASM.ProcedureTable[] procedureTable { get; set; }
        public ProtoCore.DSASM.SymbolTable[] runtimeSymbols { get; set; }

        
        public InstructionStream[] instrStreamList { get; set; } 
        public InstructionStream iStreamCanvas { get; set; }

        public DebugServices.EventSink EventSink = new DebugServices.ConsoleEventSink();

        //
        // Comment Jun: Refactor core
        //  These following properties are from core. 
        //  They were previously accessed directly from core by the runtime.
        //  They will now be part of the executable,
        //  and all runtime shall only read from executable.
        //
        public List<CodeBlock> CodeBlockList { get; set; }
        public ProtoCore.Lang.FunctionTable FunctionTable { get; set; }
        public List<CodeBlock> CompleteCodeBlockList { get; set; }
        public DynamicVariableTable DynamicVariableTable { get; set; }
        public DynamicFunctionTable DynamicFunctionTable { get; set; }
        public FunctionPointerTable FunctionPointerTable { get; set; }


        public Executable()
        {
            Reset();
        }

        public void Reset()
        {
            isSingleAssocBlock = true;
            runtimeSymbols = null;
            procedureTable = null;
            classTable = null;
            instrStreamList = null;
            iStreamCanvas = null;
            CodeBlockList = new List<CodeBlock>();
        }
    }

    public enum CodeBlockType
    {
        kLanguage,
        kConstruct,
        kFunction,
        kTypesMax
    }

    public class CodeBlock
    {
        public CodeBlockType blockType { get; set; }

        public CodeBlock parent { get; set; }
        public List<CodeBlock> children { get; set; }

        public ProtoCore.Language language { get; private set; }
        public int globsize { get; set; }
        public int codeBlockId { get; private set; }

        public ProtoCore.DSASM.SymbolTable symbolTable { get; set; }
        public ProtoCore.DSASM.ProcedureTable procedureTable { get; private set; }
        public List<AttributeEntry> Attributes { get; set; }
        public InstructionStream instrStream { get; set; }

        public DebugServices.EventSink EventSink = new DebugServices.ConsoleEventSink();

        public bool isBreakable { get; set; }

        public CodeBlock(CodeBlockType type, ProtoCore.Language langId, int codeBlockId, SymbolTable symbols, ProcedureTable procTable, bool isBreakableBlock = false, ProtoLanguage.CompileStateTracker compileState = null)
        {
            blockType = type;

            parent = null;
            children = new List<CodeBlock>();

            language = langId;
            this.codeBlockId = codeBlockId;
            instrStream = new InstructionStream(langId, compileState);

            symbolTable = symbols;
            procedureTable = procTable;

            isBreakable = isBreakableBlock;
        }

        public bool IsMyAncestorBlock(int blockId)
        {
            CodeBlock ancestor = this.parent;
            while (ancestor != null)
            {
                if (ancestor.codeBlockId == blockId)
                {
                    return true;
                }
                else
                {
                    ancestor = ancestor.parent;
                }
            }
            return false;
        }
    }
}
