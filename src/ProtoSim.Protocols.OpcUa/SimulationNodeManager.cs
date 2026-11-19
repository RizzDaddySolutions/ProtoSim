using Opc.Ua;
using Opc.Ua.Server;
using ProtoSim.Core.Data;

namespace ProtoSim.Protocols.OpcUa;

/// <summary>
/// Exposes the variables of an <see cref="OpcUaDataModel"/> as an OPC UA address space:
/// Objects/[&lt;RootFolder&gt;/]&lt;folders&gt;/&lt;variable&gt;. Each '.' or '/' in a tag address is a folder level, so
/// BOB.MARY.SUE.PV browses as Objects/BOB/MARY/SUE/PV with NodeId ns=X;s=BOB.MARY.SUE.PV.
/// Tags added or removed while running appear and disappear live.
/// </summary>
internal sealed class SimulationNodeManager : CustomNodeManager2
{
    private readonly OpcUaDataModel _model;
    private readonly OpcUaServerSettings _settings;
    private readonly Dictionary<string, FolderState> _folders = new(StringComparer.Ordinal);
    private readonly Dictionary<OpcUaVariable, BaseDataVariableState> _nodes = [];
    private FolderState? _root;

    // References from the Objects folder to top-level nodes, collected while the address space is created.
    private IList<IReference>? _objectsReferences;

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

            _objectsReferences = references;
            if (!string.IsNullOrWhiteSpace(_settings.RootFolder))
            {
                string rootName = _settings.RootFolder.Trim();
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
                AddToObjects(_root);
            }

            foreach (var v in _model.Variables) AddVariableNode(v);
            _objectsReferences = null;
        }

        _model.VariableAdded += OnVariableAdded;
        _model.VariableRemoved += OnVariableRemoved;
        _model.ValueChanged += OnValueChanged;
        _model.WritableChanged += OnWritableChanged;
    }

    /// <summary>Stops listening to the data model, which outlives the server. Safe to call more than once.</summary>
    public void Detach()
    {
        _model.VariableAdded -= OnVariableAdded;
        _model.VariableRemoved -= OnVariableRemoved;
        _model.ValueChanged -= OnValueChanged;
        _model.WritableChanged -= OnWritableChanged;
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

    private void OnWritableChanged(OpcUaVariable v)
    {
        lock (Lock)
        {
            if (!_nodes.TryGetValue(v, out var node)) return;
            node.AccessLevel = node.UserAccessLevel = AccessFor(v);
            node.ClearChangeMasks(SystemContext, false);
        }
    }

    private byte AccessFor(OpcUaVariable v) =>
        _settings.AllowWrites && v.ClientWritable ? AccessLevels.CurrentReadOrWrite : AccessLevels.CurrentRead;

    /// <summary>Links a top-level node into the standard Objects folder (also while the server is running).</summary>
    private void AddToObjects(BaseInstanceState node)
    {
        node.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
        if (node is BaseObjectState obj)
        {
            obj.EventNotifier = EventNotifiers.SubscribeToEvents;
            AddRootNotifier(obj);
        }
        AddPredefinedNode(SystemContext, node);
        var reference = new NodeStateReference(ReferenceTypeIds.Organizes, false, node.NodeId);
        if (_objectsReferences != null) _objectsReferences.Add(reference);
        // Not awaited: we hold this manager's lock, and the Objects folder belongs to the core node manager.
        else _ = Server.NodeManager.AddReferencesAsync(ObjectIds.ObjectsFolder, new List<IReference> { reference }, CancellationToken.None).AsTask();
    }

    /// <summary>The folder for a '/'-joined folder path; null means the node goes straight into Objects.</summary>
    private FolderState? GetFolder(string folderPath)
    {
        if (folderPath.Length == 0) return _root;
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
        if (parent == null)
        {
            AddToObjects(folder);
        }
        else
        {
            parent.AddChild(folder);
            AddPredefinedNode(SystemContext, folder);
        }
        _folders[folderPath] = folder;
        return folder;
    }

    private void AddVariableNode(OpcUaVariable v)
    {
        if (_nodes.ContainsKey(v)) return;
        var parent = GetFolder(v.FolderPath);
        byte access = AccessFor(v);

        var node = new BaseDataVariableState(parent)
        {
            SymbolicName = v.Name,
            ReferenceTypeId = new NodeId(ReferenceTypes.Organizes),
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            NodeId = new NodeId(v.NodeIdText, NamespaceIndex),
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
        if (parent == null)
        {
            AddToObjects(node); // a single-level address such as PV sits directly in Objects
        }
        else
        {
            parent.AddChild(node);
            AddPredefinedNode(SystemContext, node);
        }
        _nodes[v] = node;
    }

    private ServiceResult OnClientWrite(OpcUaVariable v, ref Variant value)
    {
        if (!_settings.AllowWrites || !v.ClientWritable) return StatusCodes.BadNotWritable;
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
