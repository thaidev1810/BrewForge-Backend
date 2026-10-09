using BrewForge.Domain.Recipes;

namespace BrewForge.Domain.Tests.Recipes;

/// <summary>Edges are written (step, depends on step).</summary>
public sealed class StepDependencyGraphTests
{
    private static StepDependencyGraph Graph(int nodes, params (int Step, int DependsOn)[] edges) =>
        StepDependencyGraph.FromEdges(Enumerable.Range(1, nodes), edges);

    // ---------------------------------------------------------------- topological sort

    [Fact]
    public void Independent_steps_sort_in_step_order()
    {
        Assert.Equal([1, 2, 3], Graph(3).TopologicalOrder());
    }

    [Fact]
    public void Chain_sorts_prerequisites_first()
    {
        Assert.Equal([1, 2, 3, 4], Graph(4, (2, 1), (3, 2), (4, 3)).TopologicalOrder());
    }

    [Fact]
    public void Diamond_sorts_both_branches_before_the_join()
    {
        // 2 and 3 both need 1; 4 needs both.
        Assert.Equal([1, 2, 3, 4], Graph(4, (2, 1), (3, 1), (4, 2), (4, 3)).TopologicalOrder());
    }

    [Fact]
    public void Dependency_on_a_later_step_is_still_sortable_but_not_in_step_order()
    {
        // Step 1 needs step 3: no cycle, so an order exists - it just is not 1, 2, 3.
        var order = Graph(3, (1, 3)).TopologicalOrder();

        Assert.Equal([2, 3, 1], order);
    }

    [Fact]
    public void Every_step_appears_after_all_of_its_prerequisites()
    {
        (int Step, int DependsOn)[] edges = [(5, 1), (5, 2), (6, 5), (7, 3), (7, 6), (8, 4), (9, 7), (9, 8), (10, 9)];

        var order = Graph(10, edges).TopologicalOrder()!;

        Assert.Equal(10, order.Count);
        var position = order.Select((step, index) => (step, index)).ToDictionary(x => x.step, x => x.index);
        Assert.All(edges, edge => Assert.True(position[edge.DependsOn] < position[edge.Step],
            $"step {edge.Step} came before its prerequisite {edge.DependsOn}"));
    }

    [Fact]
    public void Cycle_has_no_topological_order()
    {
        Assert.Null(Graph(3, (1, 3), (2, 1), (3, 2)).TopologicalOrder());
        Assert.Null(Graph(2, (1, 2), (2, 1)).TopologicalOrder());
    }

    [Fact]
    public void Graph_of_forty_steps_is_sorted()
    {
        var edges = Enumerable.Range(2, 39).Select(step => (Step: step, DependsOn: step - 1)).ToArray();

        Assert.Equal(Enumerable.Range(1, 40), Graph(40, edges).TopologicalOrder());
    }

    // ---------------------------------------------------------------- cycles

    [Fact]
    public void Acyclic_graph_has_no_cycles()
    {
        Assert.Empty(Graph(4, (2, 1), (3, 1), (4, 2), (4, 3)).Cycles());
    }

    [Fact]
    public void Three_step_cycle_is_reported_with_all_three_steps()
    {
        var cycles = Graph(3, (1, 3), (2, 1), (3, 2)).Cycles();

        Assert.Equal([1, 2, 3], Assert.Single(cycles));
    }

    [Fact]
    public void Steps_that_merely_come_after_a_cycle_are_not_part_of_it()
    {
        // 1-2-3 form the cycle; 4 depends on 3 and 5 on 4, but neither is on it.
        var cycles = Graph(5, (1, 3), (2, 1), (3, 2), (4, 3), (5, 4)).Cycles();

        Assert.Equal([1, 2, 3], Assert.Single(cycles));
    }

    [Fact]
    public void Steps_that_a_cycle_depends_on_are_not_part_of_it()
    {
        // 2 and 3 depend on each other; both need 1, which is innocent.
        var cycles = Graph(3, (2, 1), (2, 3), (3, 2)).Cycles();

        Assert.Equal([2, 3], Assert.Single(cycles));
    }

    [Fact]
    public void Two_separate_cycles_are_reported_separately()
    {
        var cycles = Graph(5, (1, 2), (2, 1), (4, 5), (5, 4)).Cycles();

        Assert.Equal(2, cycles.Count);
        Assert.Equal([1, 2], cycles[0]);
        Assert.Equal([4, 5], cycles[1]);
    }

    [Fact]
    public void Step_depending_on_itself_is_a_cycle_of_one()
    {
        var graph = Graph(2, (2, 2));

        Assert.Equal([2], Assert.Single(graph.Cycles()));
        Assert.Null(graph.TopologicalOrder());
    }

    [Fact]
    public void Topological_order_exists_exactly_when_there_is_no_cycle()
    {
        (int, int)[][] cases =
        [
            [], [(2, 1)], [(1, 2), (2, 1)], [(1, 3), (2, 1), (3, 2)], [(3, 1), (3, 2), (4, 3)],
            [(2, 1), (3, 2), (4, 3), (2, 4)], [(4, 1), (4, 2), (4, 3)],
        ];

        foreach (var edges in cases)
        {
            var graph = Graph(4, edges);
            Assert.Equal(graph.Cycles().Count == 0, graph.TopologicalOrder() is not null);
        }
    }
}
