namespace OurNotes.Director.Core;

// StoryRenderer.sortItems: stable sorting by order, then back-to-front distance.
public sealed record AdvRenderItem(int Order,float Distance,Action Draw)
{
    public static void DrawAll(IEnumerable<AdvRenderItem> items){foreach(var item in items.OrderBy(i=>i.Order).ThenByDescending(i=>i.Distance))item.Draw();}
}
