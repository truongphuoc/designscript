﻿
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using GraphToDSCompiler;
using ProtoCore.DSASM.Mirror;
using System.Diagnostics;
using ProtoCore.Utils;
using System.ComponentModel;
using System.Threading;
using ProtoFFI;
using ProtoCore.AssociativeGraph;
using ProtoCore.AST.AssociativeAST;
using ProtoCore.Mirror;

namespace ProtoScript.Runners
{
    public enum EventStatus
    {
        OK,
        Error,
        Warning
    }

    public struct Subtree
    {
        public Guid GUID; 
        public List<AssociativeNode> AstNodes;
    }

    public class GraphSyncData
    {
        public List<Subtree> DeletedSubtrees
        {
            get;
            private set;
        }

        public List<Subtree> AddedSubtrees
        {
            get;
            private set;
        }

        public List<Subtree> ModifiedSubtrees
        {
            get;
            private set;
        }

        public GraphSyncData(List<Subtree> deleted, List<Subtree> added, List<Subtree> modified)
        {
            DeletedSubtrees = deleted;
            AddedSubtrees = added;
            ModifiedSubtrees = modified;
        }
    }

    public interface ILiveRunner
    {
        void UpdateGraph(GraphToDSCompiler.SynchronizeData syncData);
        void BeginUpdateGraph(GraphToDSCompiler.SynchronizeData syncData);
        void BeginConvertNodesToCode(List<SnapshotNode> snapshotNodes);

        void UpdateGraph(GraphSyncData syncData);
        void BeginUpdateGraph(GraphSyncData syncData);
        void BeginConvertNodesToCode(List<Subtree> subtrees);

        void BeginQueryNodeValue(uint nodeId);
        ProtoCore.Mirror.RuntimeMirror QueryNodeValue(uint nodeId);
        ProtoCore.Mirror.RuntimeMirror QueryNodeValue(string nodeName);
        void BeginQueryNodeValue(List<uint> nodeIds);
        string GetCoreDump();

        void BeginQueryNodeValue(Guid nodeGuid);
        ProtoCore.Mirror.RuntimeMirror QueryNodeValue(Guid nodeId);
        void BeginQueryNodeValue(List<Guid> nodeGuid);
        
        event NodeValueReadyEventHandler NodeValueReady;
        event GraphUpdateReadyEventHandler GraphUpdateReady;
        event NodesToCodeCompletedEventHandler NodesToCodeCompleted;

        event DynamoNodeValueReadyEventHandler DynamoNodeValueReady;
        event DynamoGraphUpdateReadyEventHandler DynamoGraphUpdateReady;
    }

    public partial class LiveRunner : ILiveRunner
    {
        /// <summary>
        ///  These are configuration parameters passed by host application to be consumed by geometry library and persistent manager implementation. 
        /// </summary>
        public class Options
        {
            /// <summary>
            /// The configuration parameters that needs to be passed to
            /// different applications.
            /// </summary>
            public Dictionary<string, object> PassThroughConfiguration;

            /// <summary>
            /// The path of the root graph/module
            /// </summary>
            public string RootModulePathName;

            /// <summary>
            /// List of search directories to resolve any file reference
            /// </summary>
            public List<string> SearchDirectories;

            /// <summary>
            /// If the Interpreter mode is true, the LiveRunner takes in code statements as input strings
            /// and not SyncData
            /// </summary>
            public bool InterpreterMode = false;
        }

        private Dictionary<uint, string> GetModifiedGuidList()
        {
            // Retrieve the actual modified nodes 
            // Execution is complete, get all the modified guids 
            // Get the modified symbol names from the VM
            List<string> modifiedNames = this.runnerCore.Rmem.GetModifiedSymbolString();
            Dictionary<uint, string> modfiedGuidList = new Dictionary<uint, string>();
            foreach (string name in modifiedNames)
            {
                // Get the uid of the modified symbol
                if (this.graphCompiler.mapModifiedName.ContainsKey(name))
                {
                    uint id = this.graphCompiler.mapModifiedName[name];
                    if (!modfiedGuidList.ContainsKey(id))
                    {
                        // Append the modified guid into the modified list
                        modfiedGuidList.Add(this.graphCompiler.mapModifiedName[name], name);
                    }
                }
            }
            return modfiedGuidList;
        }

        private void ResetModifiedSymbols()
        {
             this.runnerCore.Rmem.ResetModifedSymbols();
        }

        private SynchronizeData CreateSynchronizeDataForGuidList(Dictionary<uint, string> modfiedGuidList)
        {
            Dictionary<uint, SnapshotNode> modifiedGuids = new Dictionary<uint, SnapshotNode>();
            SynchronizeData syncDataReturn = new SynchronizeData();

            if (modfiedGuidList != null)
            {
                //foreach (uint guid in modfiedGuidList)
                foreach (var kvp in modfiedGuidList)
                {
                    // Get the uid recognized by the graphIDE
                    uint guid = kvp.Key;
                    string name = kvp.Value;
                    SnapshotNode sNode = new SnapshotNode(this.graphCompiler.GetRealUID(guid), SnapshotNodeType.Identifier, name);
                    if (!modifiedGuids.ContainsKey(sNode.Id))
                    {
                        modifiedGuids.Add(sNode.Id, sNode);
                    }
                }

                foreach (KeyValuePair<uint, SnapshotNode> kvp in modifiedGuids)
                    syncDataReturn.ModifiedNodes.Add(kvp.Value);
            }

            return syncDataReturn;
        }

        private ProtoScriptTestRunner runner;
        private ProtoRunner.ProtoVMState vmState;
        private GraphToDSCompiler.GraphCompiler graphCompiler;
        private ProtoCore.Core runnerCore = null;
        private ProtoLanguage.CompileStateTracker compileState = null;
        private ProtoCore.Options coreOptions = null;
        private Options executionOptions = null;
        private bool syncCoreConfigurations = false;
        private int deltaSymbols = 0;
        private ProtoCore.CompileTime.Context staticContext = null;

        private readonly Object operationsMutex = new object();

        private Queue<Task> taskQueue; 

        private Thread workerThread;

        public LiveRunner()
        {
            InitRunner( new Options());
        }
        
        public LiveRunner(Options options)
        {
            InitRunner(options);
        }
        public GraphToDSCompiler.GraphCompiler GetCurrentGraphCompilerInstance()
        { 
            return graphCompiler;
        }
        private void InitRunner(Options options)
        {

            graphCompiler = GraphToDSCompiler.GraphCompiler.CreateInstance();
            graphCompiler.SetCore(GraphUtilities.GetCompilationState());
            runner = new ProtoScriptTestRunner();

            executionOptions = options;
            InitOptions();
            InitCore();


            taskQueue = new Queue<Task>();

            workerThread = new Thread(new ThreadStart(TaskExecMethod));
            

            workerThread.IsBackground = true;
            workerThread.Start();

            staticContext = new ProtoCore.CompileTime.Context();
        }

        private void InitOptions()
        {

            // Build the options required by the core
            Validity.Assert(coreOptions == null);
            coreOptions = new ProtoCore.Options();
            coreOptions.GenerateExprID = true;
            coreOptions.IsDeltaExecution = true;
            
            coreOptions.WebRunner = false;
            coreOptions.ExecutionMode = ProtoCore.ExecutionMode.Serial;
            //coreOptions.DumpByteCode = true;
            //coreOptions.Verbose = true;

            // This should have been set in the consturctor
            Validity.Assert(executionOptions != null); 
        }

        private void InitCore()
        {
            Validity.Assert(coreOptions != null);

            // Comment Jun:
            // It must be guaranteed that in delta exeuction, expression id's must not be autogerated
            // expression Id's must be propagated from the graphcompiler to the DS codegenerators
            //Validity.Assert(coreOptions.IsDeltaExecution && !coreOptions.GenerateExprID);

            runnerCore = new ProtoCore.Core(coreOptions);

            compileState = ProtoScript.CompilerUtils.BuildLiveRunnerCompilerState();
            
            SyncCoreConfigurations(runnerCore, executionOptions);


            runnerCore.Executives.Add(ProtoCore.Language.kAssociative, new ProtoAssociative.Executive(runnerCore));
            runnerCore.Executives.Add(ProtoCore.Language.kImperative, new ProtoImperative.Executive(runnerCore));
            runnerCore.FFIPropertyChangedMonitor.FFIPropertyChangedEventHandler += FFIPropertyChanged;
            vmState = null;
        }

        private void FFIPropertyChanged(FFIPropertyChangedEventArgs arg)
        {
            lock (taskQueue)
            {
                taskQueue.Enqueue(new PropertyChangedTask(this, arg.hostGraphNode));
            }
        }

        private static void SyncCoreConfigurations(ProtoCore.Core core, Options options)
        {
            if (null == options)
                return;
            //update the root module path name, if set.
            if(!string.IsNullOrEmpty(options.RootModulePathName))
                core.Options.RootModulePathName = options.RootModulePathName;
            //then update the search path, if set.
            if(null != options.SearchDirectories)
                core.Options.IncludeDirectories = options.SearchDirectories;

            //Finally update the pass thru configuration values
            if (null == options.PassThroughConfiguration)
                return;
            foreach (var item in options.PassThroughConfiguration)
            {
                core.Configurations[item.Key] = item.Value;
            }
        }


        public void SetOptions(Options options)
        {
            executionOptions = options;
            syncCoreConfigurations = true; //request syncing the configuration
        }


        #region Public Live Runner Events

        public event NodeValueReadyEventHandler NodeValueReady = null;
        public event GraphUpdateReadyEventHandler GraphUpdateReady = null;
        public event NodesToCodeCompletedEventHandler NodesToCodeCompleted = null;

        public event DynamoNodeValueReadyEventHandler DynamoNodeValueReady = null;
        public event DynamoGraphUpdateReadyEventHandler DynamoGraphUpdateReady = null;
        #endregion

        /// <summary>
        /// Push new synchronization data, returns immediately and will
        /// trigger a GraphUpdateReady when the value when the execution
        /// is completed
        /// </summary>
        /// <param name="syncData"></param>
        public void BeginUpdateGraph(SynchronizeData syncData)
        {
            lock (taskQueue)
            {
                taskQueue.Enqueue(
                    new UpdateGraphTask(syncData, this));
            }

            //Todo(Luke) add a Monitor queue to prevent having to have the 
            //work poll
        }

        public void BeginUpdateGraph(GraphSyncData syncData)
        {
            lock (taskQueue)
            {
                taskQueue.Enqueue(new DynamoUpdateGraphTask(syncData, this));
            }
        }

        /// <summary>
        /// Async call from command-line interpreter to LiveRunner
        /// </summary>
        /// <param name="cmdLineString"></param>
        public void BeginUpdateCmdLineInterpreter(string cmdLineString)
        {
            lock (taskQueue)
            {
                taskQueue.Enqueue(
                    new UpdateCmdLineInterpreterTask(cmdLineString, this));
            }
        }

        /// <summary>
        /// Takes in a list of SnapshotNode objects, condensing them into one 
        /// or more SnapshotNode objects which caller can then turn into a more 
        /// compact representation of the former SnapshotNode objects.
        /// </summary>
        /// <param name="snapshotNodes">A list of source SnapshotNode objects 
        /// from which the resulting list of SnapshotNode is to be computed.
        /// </param>
        public void BeginConvertNodesToCode(List<SnapshotNode> snapshotNodes)
        {
            if (null == snapshotNodes || (snapshotNodes.Count <= 0))
                return; // Do nothing, there's no nodes to be converted.

            lock (taskQueue)
            {
                taskQueue.Enqueue(
                    new ConvertNodesToCodeTask(snapshotNodes, this));
            }
        }

        public void BeginConvertNodesToCode(List<Subtree> subtrees)
        {
            if (null == subtrees || (subtrees.Count <= 0))
                return; // Do nothing, there's no nodes to be converted.

            lock (taskQueue)
            {
                taskQueue.Enqueue(new DynamoConvertNodesToCodeTask(subtrees, this));
            }
        }

        /// <summary>
        /// Query For a node value this will trigger a NodeValueReady callback
        /// when the value is available
        /// </summary>
        /// <param name="nodeId"></param>
        public void BeginQueryNodeValue(uint nodeId)
        {
            lock (taskQueue)
            {
                taskQueue.Enqueue(
                    new NodeValueRequestTask(nodeId, this));
            }
        }

        public void BeginQueryNodeValue(List<uint> nodeIds)
        {
            lock (taskQueue)
            {
                foreach (uint nodeId in nodeIds)
                {
                    taskQueue.Enqueue(
                        new NodeValueRequestTask(nodeId, this));
                }
            }
        }

        public void BeginQueryNodeValue(Guid nodeGuid)
        {
            lock (taskQueue)
            {
                taskQueue.Enqueue(new DynamoNodeValueRequestTask(nodeGuid, this));
            }
        }

        public void BeginQueryNodeValue(List<Guid> nodeGuids)
        {
            lock (taskQueue)
            {
                foreach (Guid nodeGuid in nodeGuids)
                {
                    taskQueue.Enqueue(
                        new DynamoNodeValueRequestTask(nodeGuid, this));
                }
            }
        }

        /// <summary>
        /// Query for a node value. This will block until the value is available.
        /// It will only serviced when all ASync calls have been completed
        /// </summary>
        /// <param name="nodeId"></param>
        /// <returns></returns>
        public ProtoCore.Mirror.RuntimeMirror QueryNodeValue(uint nodeId)
        {
            while (true)
            {
                lock (taskQueue)
                {
                    //Spin waiting for the queue to be empty
                    if (taskQueue.Count == 0)
                    {

                        //No entries and we have the lock
                        //Synchronous query to get the node

                        return InternalGetNodeValue(nodeId);
                    }
                }
                Thread.Sleep(0);
            }

        }

        public ProtoCore.Mirror.RuntimeMirror QueryNodeValue(Guid nodeGuid)
        {
            while (true)
            {
                lock (taskQueue)
                {
                    //Spin waiting for the queue to be empty
                    if (taskQueue.Count == 0)
                    {

                        //No entries and we have the lock
                        //Synchronous query to get the node

                        return InternalGetNodeValue(nodeGuid);
                    }
                }
                Thread.Sleep(0);
            }

        }

        /// <summary>
        /// VM Debugging API for general Debugging purposes 
        /// temporarily used by Cmmand Line REPL in FormitDesktop
        /// </summary>
        /// <returns></returns>
        public string GetCoreDump()
        {
            // Prints out the final Value of every symbol in the program
            // Traverse order:
            //  Exelist, Globals symbols

            StringBuilder globaltrace = null;

            ProtoCore.DSASM.Executive exec = runnerCore.CurrentExecutive.CurrentDSASMExec;
            ProtoCore.DSASM.Mirror.ExecutionMirror execMirror = new ProtoCore.DSASM.Mirror.ExecutionMirror(exec, runnerCore);
            ProtoCore.DSASM.Executable exe = exec.rmem.Executable;

            // Only display symbols defined in the default top-most langauge block;
            // Otherwise garbage information may be displayed.
            string formattedString = string.Empty;
            if (exe.runtimeSymbols.Length > 0)
            {
                int blockId = 0;

                ProtoCore.DSASM.SymbolTable symbolTable = exe.runtimeSymbols[blockId];

                for (int i = 0; i < symbolTable.symbolList.Count; ++i)
                {
                    //int n = symbolTable.symbolList.Count - 1;
                    //formatParams.ResetOutputDepth();
                    ProtoCore.DSASM.SymbolNode symbolNode = symbolTable.symbolList[i];

                    bool isLocal = ProtoCore.DSASM.Constants.kGlobalScope != symbolNode.functionIndex;
                    bool isStatic = (symbolNode.classScope != ProtoCore.DSASM.Constants.kInvalidIndex && symbolNode.isStatic);
                    if (symbolNode.isArgument || isLocal || isStatic || symbolNode.isTemp)
                    {
                        // These have gone out of scope, their values no longer exist
                        //return ((null == globaltrace) ? string.Empty : globaltrace.ToString());
                        continue;
                    }

                    ProtoCore.Runtime.RuntimeMemory rmem = exec.rmem;
                    ProtoCore.DSASM.StackValue sv = rmem.GetStackData(blockId, i, ProtoCore.DSASM.Constants.kGlobalScope);
                    formattedString = formattedString + string.Format("{0} = {1}\n", symbolNode.name, execMirror.GetStringValue(sv, rmem.Heap, blockId));

                    //if (null != globaltrace)
                    //{
                    //    int maxLength = 1020;
                    //    while (formattedString.Length > maxLength)
                    //    {
                    //        globaltrace.AppendLine(formattedString.Substring(0, maxLength));
                    //        formattedString = formattedString.Remove(0, maxLength);
                    //    }

                    //    globaltrace.AppendLine(formattedString);
                    //}
                }

                //formatParams.ResetOutputDepth();
            }

            //return ((null == globaltrace) ? string.Empty : globaltrace.ToString());
            return formattedString;
        }


        public ProtoCore.Mirror.RuntimeMirror QueryNodeValue(string nodeName)
        {
            while (true)
            {
                lock (taskQueue)
                {
                    //Spin waiting for the queue to be empty
                    if (taskQueue.Count == 0)
                    {

                        //No entries and we have the lock
                        //Synchronous query to get the node

                        return InternalGetNodeValue(nodeName);
                    }
                }
                Thread.Sleep(0);
            }

        }

        public void UpdateGraph(SynchronizeData syndData)
        {


            while (true)
            {
                lock (taskQueue)
                {
                    //Spin waiting for the queue to be empty
                    if (taskQueue.Count == 0)
                    {
                        string code = null;
                         SynchronizeInternal(syndData, out code);
                        return;

                    }
                }
                Thread.Sleep(0);
            }
            
        }

        public void UpdateGraph(GraphSyncData syncData)
        {
            while (true)
            {
                lock (taskQueue)
                {
                    if (taskQueue.Count == 0)
                    {
                        SynchronizeInternal(syncData);
                        return;
                    }
                }
            }
        }

        public void UpdateCmdLineInterpreter(string code)
        {
            while (true)
            {
                lock (taskQueue)
                {
                    //Spin waiting for the queue to be empty
                    if (taskQueue.Count == 0)
                    {
                        SynchronizeInternal(code);
                        return;
                    }
                }
                Thread.Sleep(0);
            }
        }

        //Secondary thread
        private void TaskExecMethod()
        {
            while (true)
            {
                Task task = null;

                lock (taskQueue)
                {
                    if (taskQueue.Count > 0)
                        task = taskQueue.Dequeue();
                }

                if (task != null)
                {
                        task.Execute();
                    continue;
                    
                }

                Thread.Sleep(50);

            }

        }



        #region Internal Implementation


        private ProtoCore.Mirror.RuntimeMirror InternalGetNodeValue(string varname)
        {
            Validity.Assert(null != vmState);

            // Comment Jun: all symbols are in the global block as there is no notion of scoping the the graphUI yet.
            const int blockID = 0;

            return  vmState.LookupName(varname, blockID);
        }

        private ProtoCore.Mirror.RuntimeMirror InternalGetNodeValue(uint nodeId)
        {
            //ProtoCore.DSASM.Constants.kInvalidIndex tells the UpdateUIDForCodeblock to look for the lastindex for given codeblock
            nodeId = graphCompiler.UpdateUIDForCodeblock(nodeId, ProtoCore.DSASM.Constants.kInvalidIndex);
            Validity.Assert(null != vmState);
            string varname = graphCompiler.GetVarName(nodeId);
            if (string.IsNullOrEmpty(varname))
            {
                return null;
            }
            return InternalGetNodeValue(varname);
        }

        private ProtoCore.Mirror.RuntimeMirror InternalGetNodeValue(Guid nodeGuid)
        {
            throw new NotImplementedException();
        }

        private ProtoLanguage.CompileStateTracker Compile(string code, out int blockId)
        {
            staticContext.SetData(code, new Dictionary<string, object>(), graphCompiler.ExecutionFlagList);

            compileState = runner.Compile(staticContext, runnerCore, out blockId);
            Validity.Assert(null != compileState);


            if (compileState.compileSucceeded)
            {
                // This is the boundary between compilestate and runtime core
                // Generate the executable
                compileState.GenerateExecutable();

                // Get the executable from the compileState
                runnerCore.DSExecutable = compileState.DSExecutable;

                // Update the symbol tables
                // TODO Jun: Expand to accomoadate the list of symbols
                staticContext.symbolTable = runnerCore.DSExecutable.runtimeSymbols[0];
            }
            return compileState;
        }


        private ProtoRunner.ProtoVMState Execute()
        {
            // runnerCore.GlobOffset is the number of global symbols that need to be allocated on the stack
            // The argument to Reallocate is the number of ONLY THE NEW global symbols as the stack needs to accomodate this delta
            int newSymbols = compileState.GlobOffset - deltaSymbols;

            // If there are lesser symbols to allocate for this run, then it means nodes were deleted.
            // TODO Jun: Determine if it is safe to just leave them in the global stack 
            //           as no symbols point to this memory location in the stack anyway
            if (newSymbols >= 0)
            {
                runnerCore.Rmem.ReAllocateMemory(newSymbols);
            }

            // Store the current number of global symbols
            deltaSymbols = compileState.GlobOffset;

            // Initialize the runtime context and pass it the execution delta list from the graph compiler
            ProtoCore.Runtime.Context runtimeContext = new ProtoCore.Runtime.Context();
            runtimeContext.execFlagList = graphCompiler.ExecutionFlagList;

            runner.Execute(runnerCore, runtimeContext, compileState);

            return new ProtoRunner.ProtoVMState(runnerCore, compileState);
        }

        private bool CompileAndExecute(string code)
        {
            // TODO Jun: Revisit all the Compile functions and remove the blockId out argument
            int blockId = ProtoCore.DSASM.Constants.kInvalidIndex;
            compileState = Compile(code, out blockId);
            Validity.Assert(null != compileState);
            if (compileState.compileSucceeded)
            {
                runnerCore.RunningBlock = blockId;
                vmState = Execute();
            }
            return compileState.compileSucceeded;
        }

        private void ResetVMForExecution()
        {
            runnerCore.ResetForExecution();
            compileState.ResetForExecution();
        }

        private void ResetVMForDeltaExecution()
        {
            runnerCore.ResetForDeltaExecution();
            compileState.ResetForExecution();
        }

        private void SynchronizeInternal(GraphSyncData syncData)
        {
            throw new NotImplementedException();
        }

        private void SynchronizeInternal(GraphToDSCompiler.SynchronizeData syncData, out string code)
        {
            Validity.Assert(null != runner);
            Validity.Assert(null != graphCompiler);

            if (syncData.AddedNodes.Count == 0 &&
                syncData.ModifiedNodes.Count == 0 &&
                syncData.RemovedNodes.Count == 0)
            {
                code = "";
                ResetVMForDeltaExecution();
                return;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("Begin SyncInternal: {0}", syncData);
                GraphToDSCompiler.GraphBuilder g = new GraphBuilder(syncData, graphCompiler);
                code = g.BuildGraphDAG();

                System.Diagnostics.Debug.WriteLine("SyncInternal => " + code);

                //List<string> deletedVars = new List<string>();
                ResetVMForDeltaExecution();

                //Synchronize the core configuration before compilation and execution.
                if (syncCoreConfigurations)
                {
                    SyncCoreConfigurations(runnerCore, executionOptions);
                    syncCoreConfigurations = false;
                }

                bool succeeded = CompileAndExecute(code);
                if (succeeded)
                {
                    graphCompiler.ResetPropertiesForNextExecution();
                }
            }
        }

        // TODO: Aparajit: This needs to be fixed for Command Line REPL
        private void SynchronizeInternal(string code)
        {
            Validity.Assert(null != runner);
            //Validity.Assert(null != graphCompiler);

            if (string.IsNullOrEmpty(code))
            {
                code = "";
                
                ResetVMForDeltaExecution();
                return;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("SyncInternal => " + code);

                ResetVMForDeltaExecution();

                //Synchronize the core configuration before compilation and execution.
                if (syncCoreConfigurations)
                {
                    SyncCoreConfigurations(runnerCore, executionOptions);
                    syncCoreConfigurations = false;
                }

                bool succeeded = CompileAndExecute(code);
                //if (succeeded)
                //{
                //    graphCompiler.ResetPropertiesForNextExecution();
                //}
            }
        }
        #endregion
    }
}
