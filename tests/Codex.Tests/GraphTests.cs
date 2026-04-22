using System.Linq;
using Codex.Authoring.ViewModels;
using Xunit;

namespace Codex.Tests;

public class GraphTests
{
    [Fact]
    public void GraphViewModel_Should_AddNodes()
    {
        var vm = new GraphViewModel();
        vm.AddNode("Test Location", "Location", "MapPin", "#3498db", 100, 100);

        Assert.Single(vm.Nodes);
        Assert.Equal("Test Location", vm.Nodes[0].Name);
        Assert.Equal(100, vm.Nodes[0].X);
    }

    [Fact]
    public void GraphViewModel_Should_ConnectNodes()
    {
        var vm = new GraphViewModel();
        vm.AddNode("A", "Location", "MapPin", "#3498db", 0, 0);
        vm.AddNode("B", "Location", "MapPin", "#3498db", 100, 100);

        var nodeA = vm.Nodes[0];
        var nodeB = vm.Nodes[1];

        vm.Connect(nodeA, nodeB, "Adjacent");

        Assert.Single(vm.Edges);
        Assert.Equal(nodeA, vm.Edges[0].Source);
        Assert.Equal(nodeB, vm.Edges[0].Target);
        Assert.Equal("Adjacent", vm.Edges[0].RelationType);
    }
}
