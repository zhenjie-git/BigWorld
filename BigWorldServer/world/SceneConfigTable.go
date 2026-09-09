package main

import (
	"os"

	pb "bigworld/common/pb"
)

type sceneEntry struct {
	spawnX    float64
	spawnZ    float64
	isDefault bool
}

type SceneConfigTable struct {
	entries      map[string]sceneEntry
	defaultScene string
}

var sceneConfigTable = &SceneConfigTable{
	entries: map[string]sceneEntry{
		"MainCity": {spawnX: 0, spawnZ: 0, isDefault: true},
	},
	defaultScene: "MainCity",
}

func SceneConfig() *SceneConfigTable { return sceneConfigTable }

func (t *SceneConfigTable) Load(path string) error {
	if path == "" {
		return nil
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	msg := pb.GetRootAsSceneConfigMsg(data, 0)
	entries := make(map[string]sceneEntry, msg.EntriesLength())
	defaultScene := ""
	for i := 0; i < msg.EntriesLength(); i++ {
		var e pb.SceneConfigEntry
		if !msg.Entries(&e, i) {
			continue
		}
		id := string(e.SceneId())
		if id == "" {
			continue
		}
		entries[id] = sceneEntry{
			spawnX:    float64(e.SpawnX()),
			spawnZ:    float64(e.SpawnZ()),
			isDefault: e.IsDefault(),
		}
		if e.IsDefault() {
			defaultScene = id
		}
	}
	if len(entries) == 0 {
		return nil
	}
	if defaultScene == "" {
		for id := range entries {
			defaultScene = id
			break
		}
	}
	t.entries = entries
	t.defaultScene = defaultScene
	return nil
}

func (t *SceneConfigTable) Get(sceneId string) (sceneEntry, bool) {
	e, ok := t.entries[sceneId]
	return e, ok
}

func (t *SceneConfigTable) DefaultScene() string { return t.defaultScene }
