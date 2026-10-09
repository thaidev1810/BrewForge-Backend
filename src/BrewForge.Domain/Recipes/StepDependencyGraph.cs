namespace BrewForge.Domain.Recipes;

/// <summary>
/// The directed graph of a recipe version: one node per step, one edge per
/// <c>step_dependency</c> row, pointing from a step to the step it depends
/// on. Nodes are identified by <c>step_order</c>, which is unique within a
/// version and exists before a step has a database id.
/// </summary>
public sealed class StepDependencyGraph
{
    /// <summary>For each step, the steps it depends on (its prerequisites).</summary>
    private readonly SortedDictionary<int, SortedSet<int>> _prerequisites = [];

    private StepDependencyGraph() { }

    public IReadOnlyCollection<int> Nodes => _prerequisites.Keys;

    public static StepDependencyGraph From(IEnumerable<RecipeStep> steps)
    {
        var graph = new StepDependencyGraph();
        var all = steps.ToList();
        foreach (var step in all) graph._prerequisites[step.StepOrder] = [];

        foreach (var step in all)
        {
            foreach (var dependency in step.Dependencies)
            {
                // An edge to a step outside the version is not part of this
                // graph; the ordering check reports it separately.
                if (dependency.DependsOnStep is { } target && graph._prerequisites.ContainsKey(target.StepOrder))
                {
                    graph._prerequisites[step.StepOrder].Add(target.StepOrder);
                }
            }
        }
        return graph;
    }

    /// <summary>Builds a graph from explicit edges (step, depends on step). Used by tests and by the draft parser.</summary>
    public static StepDependencyGraph FromEdges(IEnumerable<int> nodes, IEnumerable<(int Step, int DependsOn)> edges)
    {
        var graph = new StepDependencyGraph();
        foreach (var node in nodes) graph._prerequisites[node] = [];
        foreach (var (step, dependsOn) in edges)
        {
            if (graph._prerequisites.ContainsKey(step) && graph._prerequisites.ContainsKey(dependsOn))
            {
                graph._prerequisites[step].Add(dependsOn);
            }
        }
        return graph;
    }

    public IReadOnlyCollection<int> PrerequisitesOf(int step) =>
        _prerequisites.TryGetValue(step, out var set) ? set : [];

    /// <summary>
    /// Kahn's algorithm. Returns the steps in an order in which every step
    /// comes after all of its prerequisites, or null when no such order
    /// exists, that is, when the graph has a cycle.
    ///
    /// Among the steps that are ready at the same time the lowest
    /// <c>step_order</c> goes first, so the result is deterministic and, for
    /// a recipe whose dependencies all point backwards, equal to the step
    /// order itself.
    /// </summary>
    public IReadOnlyList<int>? TopologicalOrder()
    {
        var unmet = _prerequisites.ToDictionary(pair => pair.Key, pair => pair.Value.Count);
        var dependents = _prerequisites.Keys.ToDictionary(node => node, _ => new List<int>());
        foreach (var (step, prerequisites) in _prerequisites)
        {
            foreach (var prerequisite in prerequisites) dependents[prerequisite].Add(step);
        }

        var ready = new SortedSet<int>(unmet.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var order = new List<int>(_prerequisites.Count);

        while (ready.Count > 0)
        {
            var next = ready.Min;
            ready.Remove(next);
            order.Add(next);

            foreach (var dependent in dependents[next])
            {
                if (--unmet[dependent] == 0) ready.Add(dependent);
            }
        }

        // Every step on a cycle keeps at least one unmet prerequisite forever.
        return order.Count == _prerequisites.Count ? order : null;
    }

    /// <summary>
    /// Every cycle of the graph, as the set of steps on it: the strongly
    /// connected components with more than one step, plus any step that
    /// depends on itself. Found with Tarjan's algorithm, so a step that
    /// merely comes after a cycle is not reported as part of it.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<int>> Cycles()
    {
        var index = new Dictionary<int, int>();
        var lowLink = new Dictionary<int, int>();
        var onStack = new HashSet<int>();
        var stack = new Stack<int>();
        var cycles = new List<IReadOnlyList<int>>();
        var counter = 0;

        foreach (var node in _prerequisites.Keys)
        {
            if (!index.ContainsKey(node)) Visit(node);
        }
        return [.. cycles.OrderBy(cycle => cycle[0])];

        void Visit(int node)
        {
            index[node] = lowLink[node] = counter++;
            stack.Push(node);
            onStack.Add(node);

            foreach (var next in _prerequisites[node])
            {
                if (!index.ContainsKey(next))
                {
                    Visit(next);
                    lowLink[node] = Math.Min(lowLink[node], lowLink[next]);
                }
                else if (onStack.Contains(next))
                {
                    lowLink[node] = Math.Min(lowLink[node], index[next]);
                }
            }

            if (lowLink[node] != index[node]) return;

            var component = new List<int>();
            int member;
            do
            {
                member = stack.Pop();
                onStack.Remove(member);
                component.Add(member);
            } while (member != node);

            if (component.Count > 1 || _prerequisites[node].Contains(node))
            {
                component.Sort();
                cycles.Add(component);
            }
        }
    }
}
