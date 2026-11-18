using Opc.Ua;
using Opc.Ua.Server;
using ProtoSim.Core.Data;

namespace ProtoSim.Protocols.OpcUa;

/// <summary>
/// Exposes the variables of an <see cref="OpcUaDataModel"/> as an OPC UA address space:
/// Objects/&lt;RootFolder&gt;/&lt;path folders&gt;/&lt;variable&gt;, NodeId ns=X;s=&lt;path&gt;.
/// Tags added or removed while running appear and disappear live.
/// </summary>
internal sealed class SimulationNodeManager : CustomNodeManager2
{
    private readonly OpcUaDataModel _model;
    private readonly OpcUaServerSettings _settings;
    private readonly Dictionary<string, FolderState> _folders = new(StringComparer.Ordinal);
    private readonly Dictionary<OpcUaVariable, BaseDataVariableState> _nodes = [];
    private FolderState? _root;

    public SimulationNodeManager(IServerInternal server, ApplicationConfiguration configuration, OpcUaDataModel model, OpcUaServerSettings settings)
        : base(server, configuration, settings.NamespaceUri)
    {
        _model = model;
        _settings = settings;
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
                externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();

            string rootName = string.IsNullOrWhiteSpace(_settings.RootFolder) ? "ProtoSim" : _settings.RootFolder.Trim();
            _root = new FolderState(null)
            {
                SymbolicName = rootName,
                ReferenceTypeId = new NodeId(ReferenceTypes.Organizes),
                TypeDefinitionId = ObjectTypeIds.FolderType,
                NodeId = new NodeId(rootName, NamespaceIndex),
                BrowseName = new QualifiedName(rootName, NamespaceIndex),
                DisplayName = new LocalizedText("en", rootName),
                EventNotifier = EventNotifiers.SubscribeToEvents,
            };
            _root.AddReference(new NodeId(ReferenceTypes.Organizes), true, new ExpandedNodeId(ObjectIds.ObjectsFolder));
            references.Add(new NodeStateReference(new NodeId(ReferenceTypes.Organizes), false, _root));
            AddRootNotifier(_root);
            AddPredefinedNode(SystemContext, _root);

            foreach (var v in _model.Variables) AddVariableNode(v);
        }

        _model.VariableAdded += OnVariableAdded;
        _model.VariableRemoved += OnVariableRemoved;
        _model.ValueChanged += OnValueChanged;
    }

    /// <summary>Stops listening to the data model, which outlives the server. Safe to call more than once.</summary>
    public void Detach()
    {
        _model.VariableAdded -= OnVariableAdded;
        _model.VariableRemoved -= OnVariableRemoved;
        _model.ValueChanged -= OnValueChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Detach();
        base.Dispose(disposing);
    }

    private void OnVariableAdded(OpcUaVariable v)
    {
        lock (Lock) AddVariableNode(v);
    }

    private void OnVariableRemoved(OpcUaVariable v)
    {
        lock (Lock)
        {
            if (!_nodes.Remove(v, out var node)) return;
            if (node.Parent is NodeState parent) parent.RemoveChild(node);
            DeleteNode(SystemContext, node.NodeId);
        }
    }

    private void OnValueChanged(OpcUaVariable v)
    {
        lock (Lock)
        {
            if (!_nodes.TryGetValue(v, out var node)) return;
            node.Value = ToVariant(v.Value, v.DataType);
            node.Timestamp = DateTime.UtcNow;
            node.StatusCode = StatusCodes.Good;
            node.ClearChangeMasks(SystemContext, false);
        }
    }

    private FolderState GetFolder(string folderPath)
    {
        if (folderPath.Length == 0) return _root!;
        if (_folders.TryGetValue(folderPath, out var folder)) return folder;

        int slash = folderPath.LastIndexOf('/');
        var parent = GetFolder(slash < 0 ? string.Empty : folderPath[..slash]);
        string name = slash < 0 ? folderPath : folderPath[(slash + 1)..];

        folder = new FolderState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = new NodeId(ReferenceTypes.Organizes),
            TypeDefinitionId = ObjectTypeIds.FolderType,
            NodeId = new NodeId(folderPath, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = new LocalizedText("en", name),
            EventNotifier = EventNotifiers.None,
        };
        parent.AddChild(folder);
        AddPredefinedNode(SystemContext, folder);
        _folders[folderPath] = folder;
        return folder;
    }

    private void AddVariableNode(OpcUaVariable v)
    {
        if (_nodes.ContainsKey(v)) return;
        var parent = GetFolder(v.FolderPath);
        byte access = _settings.AllowWrites ? AccessLevels.CurrentReadOrWrite : AccessLevels.CurrentRead;

        var node = new BaseDataVariableState(parent)
        {
            SymbolicName = v.Name,
            ReferenceTypeId = new NodeId(ReferenceTypes.Organizes),
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            NodeId = new NodeId(v.Path, NamespaceIndex),
            BrowseName = new QualifiedName(v.Name, NamespaceIndex),
            DisplayName = new LocalizedText("en", v.Name),
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            DataType = OpcUaConfiguration.DataTypeId(v.DataType),
            ValueRank = ValueRanks.Scalar,
            AccessLevel = access,
            UserAccessLevel = access,
            MinimumSamplingInterval = _settings.MinSamplingIntervalMs,
            Historizing = false,
            Value = ToVariant(v.Value, v.DataType),
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow,
        };
        node.OnSimpleWriteValue = (ISystemContext context, NodeState n, ref Variant value) => OnClientWrite(v, ref value);
        parent.AddChild(node);
        AddPredefinedNode(SystemContext, node);
        _nodes[v] = node;
    }

    private ServiceResult OnClientWrite(OpcUaVariable v, ref Variant value)
    {
        if (!_settings.AllowWrites) return StatusCodes.BadNotWritable;
        try
        {
            var converted = ValueConverter.Coerce(value.AsBoxedObject(), v.DataType);
            OpcUaDataModel.SetRemote(v, converted);
            value = ToVariant(converted, v.DataType);
            return ServiceResult.Good;
        }
        catch (Exception)
        {
            return StatusCodes.BadTypeMismatch;
        }
    }

    private static Variant ToVariant(object value, TagDataType dataType) => dataType switch
    {
        TagDataType.Bool => Variant.From((bool)value),
        TagDataType.Int16 => Variant.From((short)value),
        TagDataType.UInt16 => Variant.From((ushort)value),
        TagDataType.Int32 => Variant.From((int)value),
        TagDataType.UInt32 => Variant.From((uint)value),
        TagDataType.Int64 => Variant.From((long)value),
        TagDataType.UInt64 => Variant.From((ulong)value),
        TagDataType.Float32 => Variant.From((float)value),
        TagDataType.Float64 => Variant.From((double)value),
        TagDataType.String => Variant.From((string)value),
        _ => throw new ArgumentOutOfRangeException(nameof(dataType)),
    };
}
