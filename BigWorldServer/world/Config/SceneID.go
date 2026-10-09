package config

const (
	MainInstance      uint32 = 0
	sceneTemplateMask        = 0xFFFF
	sceneZoneMask            = 0xFF
	sceneInstanceMask        = 0xFFFFFFFF
	MaxSceneTemplate         = sceneTemplateMask
	MaxZoneCoord             = sceneZoneMask
	MaxSceneInstance         = sceneInstanceMask
)

type SceneID int64

func MakeSceneID(template, zoneX, zoneZ int, instance uint32) SceneID {
	return SceneID(int64(uint64(template&sceneTemplateMask)<<48 |
		uint64(zoneX&sceneZoneMask)<<40 |
		uint64(zoneZ&sceneZoneMask)<<32 |
		uint64(instance&sceneInstanceMask)))
}

func SceneIDFromMain(main int32, instance uint32) SceneID {
	return SceneID(int64(main)<<32 | int64(instance&sceneInstanceMask))
}

func (id SceneID) Main() int32 {
	return int32(uint64(id) >> 32)
}

func (id SceneID) Template() int {
	return int(uint64(id) >> 48 & sceneTemplateMask)
}

func (id SceneID) ZoneX() int {
	return int(uint64(id) >> 40 & sceneZoneMask)
}

func (id SceneID) ZoneZ() int {
	return int(uint64(id) >> 32 & sceneZoneMask)
}

func (id SceneID) Instance() uint32 {
	return uint32(uint64(id) & sceneInstanceMask)
}

func (id SceneID) WithInstance(instance uint32) SceneID {
	return SceneID(int64(id)&^int64(sceneInstanceMask) | int64(instance&sceneInstanceMask))
}

func (id SceneID) Valid() bool {
	return id != 0 && id.Template() != 0
}
