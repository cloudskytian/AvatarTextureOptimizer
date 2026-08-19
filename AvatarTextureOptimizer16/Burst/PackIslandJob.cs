using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace AvatarTextureOptimizer.Burst
{
    /// <summary>
    /// Attempts to place one island's mask into an atlas mask using bottom-left-fill (BLF)
    /// with 90-degree rotations (transpose). / 用 BLF（左下优先）+ 90° 旋转（转置）尝试放置单岛掩码。
    /// Returns the placement (x,y,rotation) or (-1,-1,-1) if it does not fit.
    /// 返回放置 (x,y,rotation)；放不下返回 (-1,-1,-1)。
    /// </summary>
    [BurstCompile]
    public struct PackIslandJob : IJob
    {
        [ReadOnly] public NativeArray<byte> islandMask;   // island cells / 岛掩码
        [ReadOnly] public int islandW;
        [ReadOnly] public int islandH;
        [ReadOnly] public NativeArray<byte> atlasMask;    // atlas cells (already placed) / 图集已占掩码
        public int atlasW;
        public int atlasH;
        public int padding;                               // padding in cells / padding（单元格）

        public NativeArray<int> result;                   // [x, y, rotation]

        public void Execute()
        {
            result[0] = -1; result[1] = -1; result[2] = -1;

            for (int rot = 0; rot < 4; rot++)
            {
                int iw = (rot % 2 == 0) ? islandW : islandH;
                int ih = (rot % 2 == 0) ? islandH : islandW;
                if (iw > atlasW || ih > atlasH) continue;

                for (int gy = 0; gy + ih <= atlasH; gy++)
                for (int gx = 0; gx + iw <= atlasW; gx++)
                {
                    if (CanPlace(gx, gy, rot, iw, ih))
                    {
                        result[0] = gx; result[1] = gy; result[2] = rot;
                        return;
                    }
                }
            }
        }

        private bool CanPlace(int ox, int oy, int rot, int iw, int ih)
        {
            for (int y = 0; y < ih; y++)
            for (int x = 0; x < iw; x++)
            {
                int sx, sy;
                switch (rot)
                {
                    case 1: sx = y; sy = islandW - 1 - x; break;   // 90
                    case 2: sx = islandW - 1 - x; sy = islandH - 1 - y; break; // 180
                    case 3: sx = islandH - 1 - y; sy = x; break;   // 270
                    default: sx = x; sy = y; break;
                }
                if (islandMask[sy * islandW + sx] == 0) continue;

                for (int py = -padding; py <= padding; py++)
                for (int px = -padding; px <= padding; px++)
                {
                    int ax = ox + x + px;
                    int ay = oy + y + py;
                    if (ax < 0 || ay < 0 || ax >= atlasW || ay >= atlasH) return false;
                    if (atlasMask[ay * atlasW + ax] != 0) return false;
                }
            }
            return true;
        }
    }
}
