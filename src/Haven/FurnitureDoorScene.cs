using System.Numerics;
using Haven.Core;

namespace Haven;

// State belongs to a placed furnishing, never the cached prefab or the house save.
internal sealed class FurnitureDoorScene(FurnitureCatalogue catalogue)
{
    private sealed record Door(Furnishing Item, int Index, PrefabDoor Definition, Matrix4x4 Matrix, DoorMotionState State);
    private readonly Dictionary<(Guid, int), Door> doors = [];
    private House? bound;
    private Guid? moving;
    public bool HasDoors => doors.Count > 0;
    public void Bind(House house, Guid? movingId)
    {
        if (ReferenceEquals(bound, house) && moving == movingId) return;
        bound = house; moving = movingId;
        HashSet<(Guid, int)> keep = [];
        var parent = FurnitureCatalogue.HouseMatrix(house);
        foreach (var item in house.Furnishings)
        {
            if (item.Id == movingId) continue;
            var prefab = catalogue.Prefab(item);
            for (var i = 0; i < prefab.Doors.Length; i++)
            {
                var key = (item.Id, i); keep.Add(key);
                var matrix = FurnitureCatalogue.ItemMatrix(item) * parent;
                if (!doors.TryGetValue(key, out var old) || old.Definition != prefab.Doors[i] || old.Matrix != matrix)
                    doors[key] = new(item, i, prefab.Doors[i], matrix, new());
                else if (!ReferenceEquals(old.Item, item)) doors[key] = old with { Item = item };
            }
        }
        foreach (var key in doors.Keys.Where(k => !keep.Contains(k)).ToArray()) doors.Remove(key);
    }
    // opens: whether the item's door answers anyone at all (the owner may turn a hidden door off: it then stays shut and solid).
    public void Update(IReadOnlyList<Vector3> occupants, long now, Func<Furnishing, bool>? opens = null)
    {
        foreach (var door in doors.Values)
        {
            var occupied = false;
            if (opens?.Invoke(door.Item) == false) { door.State.Update(false, now, door.Definition.Action.Seconds); continue; }
            // Body/feet tolerance avoids flickering exactly at the floor and small
            // position jitter at a trigger edge; another floor remains outside the volume.
            foreach (var person in occupants)
                if (door.Definition.Trigger.ContainsCharacter(person, door.Matrix, door.State.TargetOpen ? .08f : .01f)) { occupied = true; break; }
            door.State.Update(occupied, now, door.Definition.Action.Seconds);
        }
    }
    public float Progress(Guid item, int door) => doors.TryGetValue((item, door), out var value) ? value.State.Progress : 0;
    public bool Blocks(Guid item, int door) => !doors.TryGetValue((item, door), out var value) || value.State.Blocks;
    public void Clear() { doors.Clear(); bound = null; moving = null; }
}
