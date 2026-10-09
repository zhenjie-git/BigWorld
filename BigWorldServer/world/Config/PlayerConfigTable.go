package config

import (
	"fmt"
	"os"

	pb "bigworld/common/pb"
)

type PlayerConfigTable struct {
	maxStep           float64
	sprintToRunTime   float64
	fallGravityMps2   float64
	fallSpeedLimitMps float64
	playerHeight      float64
	playerCenterY     float64
}

var playerConfigTable = &PlayerConfigTable{}

func PlayerConfig() *PlayerConfigTable { return playerConfigTable }

func (c *PlayerConfigTable) Load(path string) error {
	if path == "" {
		return fmt.Errorf("player config path is empty")
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	msg := pb.GetRootAsPlayerConfigMsg(data, 0)
	c.maxStep = float64(msg.VoxelMaxStepHeight())
	c.sprintToRunTime = float64(msg.SprintToRunTime())
	c.fallGravityMps2 = float64(msg.Gravity())
	c.fallSpeedLimitMps = float64(msg.FallSpeedLimit())
	c.playerHeight = float64(msg.ColliderHeight())
	c.playerCenterY = float64(msg.ColliderCenterY())

	for _, check := range []struct {
		name  string
		value float64
	}{
		{"voxel_max_step_height", c.maxStep},
		{"sprint_to_run_time", c.sprintToRunTime},
		{"gravity", c.fallGravityMps2},
		{"fall_speed_limit", c.fallSpeedLimitMps},
		{"collider_height", c.playerHeight},
		{"collider_center_y", c.playerCenterY},
	} {
		if check.value <= 0 {
			return fmt.Errorf("player config: %s must be > 0, got %v", check.name, check.value)
		}
	}
	return nil
}

func (c *PlayerConfigTable) MaxStep() float64           { return c.maxStep }
func (c *PlayerConfigTable) SprintToRunTime() float64   { return c.sprintToRunTime }
func (c *PlayerConfigTable) FallGravityMps2() float64   { return c.fallGravityMps2 }
func (c *PlayerConfigTable) FallSpeedLimitMps() float64 { return c.fallSpeedLimitMps }
func (c *PlayerConfigTable) PlayerHeight() float64      { return c.playerHeight }
func (c *PlayerConfigTable) PlayerCenterY() float64     { return c.playerCenterY }
