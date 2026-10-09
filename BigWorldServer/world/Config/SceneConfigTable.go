package config

import (
	"fmt"
	"os"
	"sort"

	pb "bigworld/common/pb"
)

type SceneEntry struct {
	Name      string
	Template  int
	SpawnX    float64
	SpawnZ    float64
	IsDefault bool
	GridX     int
	GridZ     int
	OriginX   float64
	OriginZ   float64
	ZoneSize  float64
	MainID    int32
}

func (e SceneEntry) Zoned() bool { return e.ZoneSize > 0 }

type SceneConfigTable struct {
	entries      map[int32]SceneEntry
	byName       map[string]int32
	templates    map[int]string
	defaultMain  int32
	defaultName  string
	nextInstance map[int32]uint32
}

var sceneConfigTable = &SceneConfigTable{
	entries:      make(map[int32]SceneEntry),
	byName:       make(map[string]int32),
	templates:    make(map[int]string),
	nextInstance: make(map[int32]uint32),
}

func SceneConfig() *SceneConfigTable { return sceneConfigTable }

func (t *SceneConfigTable) Load(path string) error {
	if path == "" {
		return fmt.Errorf("scene config path is empty")
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	msg := pb.GetRootAsSceneConfigMsg(data, 0)
	entries := make(map[int32]SceneEntry, msg.EntriesLength())
	byName := make(map[string]int32, msg.EntriesLength())
	templates := make(map[int]string)
	defaultMain := int32(0)
	defaultName := ""
	for i := 0; i < msg.EntriesLength(); i++ {
		var e pb.SceneConfigEntry
		if !msg.Entries(&e, i) {
			continue
		}
		name := string(e.SceneId())
		if name == "" {
			continue
		}
		entry := SceneEntry{
			Name:      name,
			Template:  int(e.TemplateId()),
			SpawnX:    float64(e.SpawnX()),
			SpawnZ:    float64(e.SpawnZ()),
			IsDefault: e.IsDefault(),
			GridX:     int(e.GridX()),
			GridZ:     int(e.GridZ()),
			OriginX:   float64(e.OriginX()),
			OriginZ:   float64(e.OriginZ()),
			ZoneSize:  float64(e.ZoneSize()),
		}
		entry.MainID = MakeSceneID(entry.Template, entry.GridX, entry.GridZ, MainInstance).Main()
		entries[entry.MainID] = entry
		byName[name] = entry.MainID
		templates[entry.Template] = name
		if e.IsDefault() {
			defaultMain = entry.MainID
			defaultName = name
		}
	}
	if len(entries) == 0 {
		return nil
	}
	if defaultMain == 0 {
		for mainID := range entries {
			defaultMain = mainID
			defaultName = entries[mainID].Name
			break
		}
	}
	t.entries = entries
	t.byName = byName
	t.templates = templates
	t.defaultMain = defaultMain
	t.defaultName = defaultName
	return nil
}

func (t *SceneConfigTable) Get(id SceneID) (SceneEntry, bool) {
	if !id.Valid() {
		return SceneEntry{}, false
	}
	e, ok := t.entries[id.Main()]
	return e, ok
}

func (t *SceneConfigTable) GetByName(name string) (SceneEntry, bool) {
	mainID, ok := t.byName[name]
	if !ok {
		return SceneEntry{}, false
	}
	e, ok := t.entries[mainID]
	return e, ok
}

func (t *SceneConfigTable) NameOf(id SceneID) string {
	if e, ok := t.Get(id); ok {
		return e.Name
	}
	if id.Template() != 0 {
		if name, ok := t.templates[id.Template()]; ok {
			return name
		}
	}
	return "unknown"
}

func (t *SceneConfigTable) MapNameOf(id SceneID) string {
	if name, ok := t.templates[id.Template()]; ok {
		return name
	}
	return t.NameOf(id)
}

func (t *SceneConfigTable) DefaultSceneID() SceneID {
	return SceneIDFromMain(t.defaultMain, MainInstance)
}

func (t *SceneConfigTable) DefaultSceneName() string { return t.defaultName }

func (t *SceneConfigTable) AllSceneIDs() []SceneID {
	ids := make([]SceneID, 0, len(t.entries))
	for mainID := range t.entries {
		ids = append(ids, SceneIDFromMain(mainID, MainInstance))
	}
	sort.Slice(ids, func(i, j int) bool { return ids[i].Main() < ids[j].Main() })
	return ids
}

func (t *SceneConfigTable) NextInstance(id SceneID) SceneID {
	t.nextInstance[id.Main()]++
	return id.WithInstance(t.nextInstance[id.Main()])
}

func (t *SceneConfigTable) ZoneAt(id SceneID, x, z float64) (SceneID, bool) {
	entry, ok := t.Get(id)
	if !ok || !entry.Zoned() {
		return 0, false
	}
	mapOriginX := entry.OriginX - float64(entry.GridX)*entry.ZoneSize
	mapOriginZ := entry.OriginZ - float64(entry.GridZ)*entry.ZoneSize
	gx := int(FloorDiv(x-mapOriginX, entry.ZoneSize))
	gz := int(FloorDiv(z-mapOriginZ, entry.ZoneSize))
	if gx < 0 || gx > MaxZoneCoord || gz < 0 || gz > MaxZoneCoord {
		return 0, false
	}
	target := MakeSceneID(entry.Template, gx, gz, id.Instance())
	if _, ok := t.entries[target.Main()]; !ok {
		return 0, false
	}
	return target, true
}

func (t *SceneConfigTable) NeighborZones(id SceneID) []SceneID {
	entry, ok := t.Get(id)
	if !ok || !entry.Zoned() {
		return nil
	}
	var neighbors []SceneID
	for dx := -1; dx <= 1; dx++ {
		for dz := -1; dz <= 1; dz++ {
			if dx == 0 && dz == 0 {
				continue
			}
			gx := entry.GridX + dx
			gz := entry.GridZ + dz
			if gx < 0 || gx > MaxZoneCoord || gz < 0 || gz > MaxZoneCoord {
				continue
			}
			neighbor := MakeSceneID(entry.Template, gx, gz, id.Instance())
			if _, ok := t.entries[neighbor.Main()]; ok {
				neighbors = append(neighbors, neighbor)
			}
		}
	}
	return neighbors
}

func FloorDiv(v, step float64) float64 {
	if step <= 0 {
		return 0
	}
	q := v / step
	if q < 0 && q != float64(int64(q)) {
		return float64(int64(q) - 1)
	}
	return q
}
