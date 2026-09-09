package main

import (
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

var playerConfigTable = &PlayerConfigTable{
	maxStep:           0.5,
	sprintToRunTime:   1.0,
	fallGravityMps2:   10.0,
	fallSpeedLimitMps: 15.0,
	playerHeight:      1.8,
	playerCenterY:     0.9,
}

func PlayerConfig() *PlayerConfigTable { return playerConfigTable }

func (c *PlayerConfigTable) Load(path string) error {
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

	if c.maxStep <= 0 {
		c.maxStep = 0.5
	}
	if c.sprintToRunTime <= 0 {
		c.sprintToRunTime = 1.0
	}
	if c.fallGravityMps2 <= 0 {
		c.fallGravityMps2 = 10.0
	}
	if c.fallSpeedLimitMps <= 0 {
		c.fallSpeedLimitMps = 15.0
	}
	if c.playerHeight <= 0 {
		c.playerHeight = 1.8
	}
	if c.playerCenterY <= 0 {
		c.playerCenterY = 0.9
	}
	return nil
}

func (c *PlayerConfigTable) MaxStep() float64           { return c.maxStep }
func (c *PlayerConfigTable) SprintToRunTime() float64   { return c.sprintToRunTime }
func (c *PlayerConfigTable) FallGravityMps2() float64   { return c.fallGravityMps2 }
func (c *PlayerConfigTable) FallSpeedLimitMps() float64 { return c.fallSpeedLimitMps }
func (c *PlayerConfigTable) PlayerHeight() float64      { return c.playerHeight }
func (c *PlayerConfigTable) PlayerCenterY() float64     { return c.playerCenterY }
