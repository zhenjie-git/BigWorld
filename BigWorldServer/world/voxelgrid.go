package main

import (
	"encoding/binary"
	"fmt"
	"log"
	"math"
	"os"
)

const (
	voxelFlagSameLayer  = 0
	voxelFlagLayerAbove = 1
	voxelFlagLayerBelow = 2
	voxelFlagBlocked    = 3

	voxelDirUp         = 0
	voxelDirDown       = 1
	voxelDirLeft       = 2
	voxelDirRight      = 3
	voxelDirUpperLeft  = 4
	voxelDirLowerLeft  = 5
	voxelDirUpperRight = 6
	voxelDirLowerRight = 7

	maxMoveDelta   = 0.6
	airborneMargin = 1.0
	airborneCeilH  = 3.0

	maxMoveWindowMs = 300
)

func voxelGetFlag(connectivity uint16, dir int) byte {
	return byte((connectivity >> (dir * 2)) & 3)
}

func voxelDirectionIndex(dx, dz int) int {
	if dx < -1 {
		dx = -1
	}
	if dx > 1 {
		dx = 1
	}
	if dz < -1 {
		dz = -1
	}
	if dz > 1 {
		dz = 1
	}
	switch {
	case dx == 0 && dz == 1:
		return voxelDirUp
	case dx == 0 && dz == -1:
		return voxelDirDown
	case dx == -1 && dz == 0:
		return voxelDirLeft
	case dx == 1 && dz == 0:
		return voxelDirRight
	case dx == -1 && dz == 1:
		return voxelDirUpperLeft
	case dx == -1 && dz == -1:
		return voxelDirLowerLeft
	case dx == 1 && dz == 1:
		return voxelDirUpperRight
	case dx == 1 && dz == -1:
		return voxelDirLowerRight
	default:
		return -1
	}
}

type voxelData struct {
	minY, maxY   float64
	connectivity uint16
}

type voxelGrid struct {
	dimX, dimZ int
	voxelSize  [3]float64
	origin     [3]float64
	columns    []int
	starts     []int
	voxels     []voxelData
	width      float64
	height     float64
	maxTopY    float64
}

func loadVoxelGrid(path string) (*voxelGrid, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}

	const headerLen = 52
	if len(data) < headerLen {
		return nil, fmt.Errorf("voxel file too small: %d bytes", len(data))
	}
	le := binary.LittleEndian
	if magic := le.Uint32(data[0:4]); magic != 0x4C584F56 {
		return nil, fmt.Errorf("invalid voxel magic 0x%08X (want \"VOXL\")", magic)
	}
	if version := le.Uint32(data[4:8]); version != 2 {
		return nil, fmt.Errorf("unsupported voxel version %d (want 2)", version)
	}

	g := &voxelGrid{
		dimX: int(int32(le.Uint32(data[8:12]))),
		dimZ: int(int32(le.Uint32(data[12:16]))),
	}
	if g.dimX <= 0 || g.dimZ <= 0 {
		return nil, fmt.Errorf("invalid grid dims %dx%d", g.dimX, g.dimZ)
	}
	for i := 0; i < 3; i++ {
		g.voxelSize[i] = float64(math.Float32frombits(le.Uint32(data[16+i*4 : 20+i*4])))
	}
	for i := 0; i < 3; i++ {
		g.origin[i] = float64(math.Float32frombits(le.Uint32(data[28+i*4 : 32+i*4])))
	}
	totalVoxels := int(int32(le.Uint32(data[40:44])))

	cols := g.dimX * g.dimZ
	expected := headerLen + cols*4 + totalVoxels*10
	if len(data) != expected {
		return nil, fmt.Errorf("voxel file size mismatch: got %d bytes, want %d (dimX=%d dimZ=%d voxels=%d)",
			len(data), expected, g.dimX, g.dimZ, totalVoxels)
	}

	off := headerLen
	g.columns = make([]int, cols)
	g.starts = make([]int, cols)
	running := 0
	for i := 0; i < cols; i++ {
		cnt := int(int32(le.Uint32(data[off : off+4])))
		off += 4
		g.columns[i] = cnt
		g.starts[i] = running
		running += cnt
	}
	if running != totalVoxels {
		return nil, fmt.Errorf("voxel count mismatch: header=%d, columns sum=%d", totalVoxels, running)
	}

	g.voxels = make([]voxelData, totalVoxels)
	maxTop := math.Inf(-1)
	for i := 0; i < totalVoxels; i++ {
		g.voxels[i] = voxelData{
			minY:         float64(math.Float32frombits(le.Uint32(data[off : off+4]))),
			maxY:         float64(math.Float32frombits(le.Uint32(data[off+4 : off+8]))),
			connectivity: le.Uint16(data[off+8 : off+10]),
		}
		if g.voxels[i].maxY > maxTop {
			maxTop = g.voxels[i].maxY
		}
		off += 10
	}
	if totalVoxels == 0 {
		maxTop = 0
	}
	g.maxTopY = maxTop

	g.width = float64(g.dimX) * g.voxelSize[0]
	g.height = float64(g.dimZ) * g.voxelSize[2]
	return g, nil
}

func (g *voxelGrid) gridIndex(x, z int) int { return x + z*g.dimX }

func (g *voxelGrid) columnCount(x, z int) int { return g.columns[g.gridIndex(x, z)] }

func (g *voxelGrid) worldToColumn(wx, wz float64) (int, int, bool) {
	x := int(math.Floor((wx - g.origin[0]) / g.voxelSize[0]))
	z := int(math.Floor((wz - g.origin[2]) / g.voxelSize[2]))
	if x < 0 || x >= g.dimX || z < 0 || z >= g.dimZ {
		return 0, 0, false
	}
	return x, z, true
}

func (g *voxelGrid) columnCenter(x, z int) (float64, float64) {
	return g.origin[0] + (float64(x)+0.5)*g.voxelSize[0],
		g.origin[2] + (float64(z)+0.5)*g.voxelSize[2]
}

func (g *voxelGrid) resolveSpawn(sx, sz float64) (float64, float64, int) {
	startX, startZ, ok := g.worldToColumn(sx, sz)
	if !ok {
		startX, startZ = g.dimX/2, g.dimZ/2
	}
	for radius := 0; radius <= 1000; radius++ {
		for dx := -radius; dx <= radius; dx++ {
			for dz := -radius; dz <= radius; dz++ {
				if abs(dx) != radius && abs(dz) != radius {
					continue
				}
				x, z := startX+dx, startZ+dz
				if x < 0 || x >= g.dimX || z < 0 || z >= g.dimZ {
					continue
				}
				if cnt := g.columnCount(x, z); cnt > 0 {
					wx, wz := g.columnCenter(x, z)
					return wx, wz, cnt - 1
				}
			}
		}
	}
	return sx, sz, -1
}

func (g *voxelGrid) topLayerAt(x, z int) int {
	return g.columnCount(x, z) - 1
}

func (g *voxelGrid) validateMove(fromX, fromZ float64, curK int, dx, dz, maxStep float64) (bool, int) {
	curX, curZ, ok := g.worldToColumn(fromX, fromZ)
	if !ok {
		return false, curK
	}
	targetX, targetZ, ok := g.worldToColumn(fromX+dx, fromZ+dz)
	if !ok {
		return false, curK
	}
	if abs(targetX-curX) > 1 || abs(targetZ-curZ) > 1 {
		return false, curK
	}

	targetIdx := g.gridIndex(targetX, targetZ)
	targetCount := g.columns[targetIdx]
	if targetCount == 0 {
		return false, curK
	}

	curIdx := g.gridIndex(curX, curZ)
	curCount := g.columns[curIdx]
	if curK < 0 || curK >= curCount {
		return false, curK
	}
	curVoxel := g.voxels[g.starts[curIdx]+curK]

	resolvedK := curK
	if targetX != curX || targetZ != curZ {
		resolvedK = g.resolveTargetLayer(curX, curZ, curK, targetX, targetZ)
		if resolvedK < 0 || resolvedK >= targetCount {
			return false, curK
		}
	}

	targetVoxel := g.voxels[g.starts[targetIdx]+resolvedK]
	heightDiff := targetVoxel.maxY - curVoxel.maxY
	if heightDiff > maxStep {
		return false, curK
	}
	return true, resolvedK
}

func (g *voxelGrid) surfaceHeight(x, z float64, k int) float64 {
	if k < 0 {
		return g.origin[1]
	}
	cx, cz, ok := g.worldToColumn(x, z)
	if !ok {
		return g.origin[1]
	}
	idx := g.gridIndex(cx, cz)
	if k >= g.columnCount(cx, cz) {
		return g.origin[1]
	}
	return g.origin[1] + g.voxels[g.starts[idx]+k].maxY
}

func (g *voxelGrid) validateGroundMove(fromX, fromZ float64, curK int, dx, dz, maxStep, maxDelta float64) (bool, int, float64) {
	if math.Hypot(dx, dz) > maxDelta {
		return false, curK, g.origin[1]
	}
	dist := math.Hypot(dx, dz)
	steps := int(math.Ceil(dist / (g.voxelSize[0] * 0.5)))
	if steps < 1 {
		steps = 1
	}
	px, pz, k := fromX, fromZ, curK
	for i := 1; i <= steps; i++ {
		nx := fromX + dx*float64(i)/float64(steps)
		nz := fromZ + dz*float64(i)/float64(steps)
		ok, nk := g.validateMove(px, pz, k, nx-px, nz-pz, maxStep)
		if !ok {
			return false, curK, g.origin[1]
		}
		px, pz, k = nx, nz, nk
	}
	return true, k, g.surfaceHeight(px, pz, k)
}

func (g *voxelGrid) resolveLayerNearY(x, z float64, refY float64) int {
	cx, cz, ok := g.worldToColumn(x, z)
	if !ok {
		return -1
	}
	idx := g.gridIndex(cx, cz)
	cnt := g.columnCount(cx, cz)
	if cnt == 0 {
		return -1
	}
	best := 0
	bestDist := math.MaxFloat64
	for i := 0; i < cnt; i++ {
		d := math.Abs(g.origin[1] + g.voxels[g.starts[idx]+i].maxY - refY)
		if d < bestDist {
			bestDist = d
			best = i
		}
	}
	return best
}

func (g *voxelGrid) validateAirborne(fromX, fromZ float64, dx, dz, y, maxDelta float64) bool {
	if math.Hypot(dx, dz) > maxDelta {
		return false
	}
	nx, nz := fromX+dx, fromZ+dz
	if _, _, ok := g.worldToColumn(nx, nz); !ok {
		return false
	}
	if y < g.origin[1]-airborneMargin || y > g.origin[1]+g.maxTopY+airborneCeilH {
		return false
	}
	return true
}

func (g *voxelGrid) resolveTargetLayer(curX, curZ, curK, targetX, targetZ int) int {
	curIdx := g.gridIndex(curX, curZ)
	targetIdx := g.gridIndex(targetX, targetZ)
	targetCount := g.columns[targetIdx]
	targetStart := g.starts[targetIdx]
	curVoxel := g.voxels[g.starts[curIdx]+curK]

	dir := voxelDirectionIndex(targetX-curX, targetZ-curZ)
	flag := voxelGetFlag(curVoxel.connectivity, dir)

	switch flag {
	case voxelFlagSameLayer:
		if curK < targetCount {
			return curK
		}
		return -1
	case voxelFlagLayerAbove:
		if curK+1 < targetCount {
			return curK + 1
		}
		return -1
	case voxelFlagLayerBelow:
		if curK-1 >= 0 {
			return curK - 1
		}
		return -1
	default:
		best := -1
		bestDist := math.MaxFloat64
		for i := 0; i < targetCount; i++ {
			d := math.Abs(g.voxels[targetStart+i].maxY - curVoxel.maxY)
			if d < bestDist {
				bestDist = d
				best = i
			}
		}
		return best
	}
}

// ceilingHit mirrors the client's CheckCeilingVoxel: it reports whether a voxel
// in the column under (x,z) overlaps the character's vertical range and returns
// the lowest such voxel's bottom surface (world Y).
func (g *voxelGrid) ceilingHit(x, z, feetY, headTopY float64) (bool, float64) {
	cx, cz, ok := g.worldToColumn(x, z)
	if !ok {
		return false, 0
	}
	idx := g.gridIndex(cx, cz)
	cnt := g.columnCount(cx, cz)
	if cnt == 0 {
		return false, 0
	}
	ceiling := math.MaxFloat64
	found := false
	for i := 0; i < cnt; i++ {
		v := g.voxels[g.starts[idx]+i]
		minY := g.origin[1] + v.minY
		maxY := g.origin[1] + v.maxY
		if minY < headTopY && maxY > feetY {
			if minY < ceiling {
				ceiling = minY
				found = true
			}
		}
	}
	return found, ceiling
}

func (g *voxelGrid) debugDump() {
	occupied := 0
	for _, c := range g.columns {
		if c > 0 {
			occupied++
		}
	}
	log.Printf("[voxelgrid] %dx%d grid, %d voxels, %d/%d columns occupied, world %.1fx%.1fm, origin (%.2f, %.2f, %.2f)",
		g.dimX, g.dimZ, len(g.voxels), occupied, len(g.columns), g.width, g.height, g.origin[0], g.origin[1], g.origin[2])
}

func abs(x int) int {
	if x < 0 {
		return -x
	}
	return x
}
