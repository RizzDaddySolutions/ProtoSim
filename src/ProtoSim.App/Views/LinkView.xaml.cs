using System.Windows.Controls;
using ProtoSim.Core.Data;

namespace ProtoSim.App.Views;

public partial class LinkView : UserControl
{
    public static TagDataType[] DataTypes { get; } = Enum.GetValues<TagDataType>();

    public LinkView()
    {
        InitializeComponent();
    }
}
