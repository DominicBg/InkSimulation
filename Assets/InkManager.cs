using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UI;

public class InkManager : MonoBehaviour
{
    public struct InkCell
    {
        public float inkLevel;
        public float inkSaturationLevel;
        public float incomingInk;

        public float InkRatio => math.saturate(inkLevel / inkSaturationLevel);
        public bool IsOverflowing => inkLevel > inkSaturationLevel;
        public float OverflowAmount => math.max(inkLevel - inkSaturationLevel, 0);
        public float OverflowAmountQuarter => OverflowAmount / 4f;
    }

    public RawImage image;

    public Texture2D heightMapRef;

    Texture2D textureCpu;
    RenderTexture visualTextureCompute;
    public int2 gridSize;
    public float inkDrop = 5;
    public float maxInkPerCell = 10;
    public float inkTransferSpeed = 2;
    NativeGrid<InkCell> inkGrid;

    int2 prevMousePos;
    int4 mouseDelta;

    RenderTexture inkGridTexture;
    public ComputeShader computeShader;

    int2x4 directions = new int2x4(
        new int2(-1, 0),
        new int2(0, 1),
        new int2(1, 0),
        new int2(0, -1));

    private void Start()
    {
        //compute
        inkGridTexture = new RenderTexture(gridSize.x, gridSize.y, 1);
        inkGridTexture.enableRandomWrite = true;
        visualTextureCompute = new RenderTexture(gridSize.x, gridSize.y, 1);
        visualTextureCompute.enableRandomWrite = true;
        visualTextureCompute.useMipMap = true;

        textureCpu = new Texture2D(gridSize.x, gridSize.y);

        image.texture = computeShader != null ? visualTextureCompute : textureCpu;

        // visualTextureCompute.
        inkGrid = new NativeGrid<InkCell>(gridSize, Allocator.Persistent);

        bool hasHeightMap = heightMapRef != null;
        for (int x = 0; x < gridSize.x; x++)
        {
            for (int y = 0; y < gridSize.y; y++)
            {
                var cell = inkGrid[x, y];
                cell.inkSaturationLevel = hasHeightMap ? (heightMapRef.GetPixel(x, y).r * maxInkPerCell) : UnityEngine.Random.Range(0f, maxInkPerCell);
                inkGrid[x, y] = cell;
            }
        }

        int initialeKernel = computeShader.FindKernel("InitializeKernel");
        int3 threadGroupSize = new int3(gridSize.x / 8 + 1, gridSize.y / 8 + 1, 1);
        computeShader.SetTexture(initialeKernel, "inkGrid", inkGridTexture);
        computeShader.SetTexture(initialeKernel, "heightMapRef", heightMapRef, 0);
        computeShader.SetFloat(nameof(maxInkPerCell), maxInkPerCell);
        computeShader.SetVector(nameof(gridSize), new Vector4(gridSize.x, gridSize.y, 0, 0));
        computeShader.Dispatch(initialeKernel, threadGroupSize.x, threadGroupSize.y, threadGroupSize.z);
    }

    private void OnDestroy()
    {
        inkGrid.Dispose();
    }

    public static bool InBound(int2 index, int2 gridSize)
    {
        return index.x >= 0 && index.x < gridSize.x && index.y >= 0 && index.y < gridSize.y;
    }

    // Update is called once per frame
    void Update()
    {
        if(Input.GetMouseButtonDown(0))
        {
            prevMousePos = MouseToIndexPos();
        }

        if (Input.GetMouseButton(0))
        {
            int2 mousePos = MouseToIndexPos();

            if (computeShader == null)
            {
                //DDA https://www.geeksforgeeks.org/dda-line-generation-algorithm-computer-graphics/
                int2 delta = mousePos - prevMousePos;
                int2 deltaAbs = math.abs(delta);
                int steps = math.cmax(deltaAbs);
                //float2 dxy = (float2)delta / steps;
                for (int i = 0; i < steps; i++)
                {
                    int2 linePos = (int2)math.lerp(prevMousePos, mousePos, i / (float)steps);
                    var cell = inkGrid[linePos];
                    cell.inkLevel += inkDrop * Time.deltaTime;
                    inkGrid[linePos] = cell;
                }
            }

            mouseDelta = new int4(prevMousePos.x, prevMousePos.y, mousePos.x, mousePos.y);
            //Debug.Log(mouseDelta);
            prevMousePos = mousePos;
        }

        if(computeShader != null)
        {
            int applyInkKernel = computeShader.FindKernel("ApplyInkKernel");
            int calculateInkTransferKernel = computeShader.FindKernel("CalculateInkTransferKernel");
            int resolveInkKernel = computeShader.FindKernel("ResolveInkKernel");

            computeShader.SetTexture(applyInkKernel, "inkGrid", inkGridTexture);
            computeShader.SetTexture(calculateInkTransferKernel, "inkGrid", inkGridTexture);
            computeShader.SetTexture(resolveInkKernel, "inkGrid", inkGridTexture);

            computeShader.SetTexture(resolveInkKernel, "visualGrid", visualTextureCompute);

            computeShader.SetFloat(nameof(inkDrop), inkDrop);
            computeShader.SetFloat(nameof(maxInkPerCell), maxInkPerCell);
            computeShader.SetFloat(nameof(inkTransferSpeed), inkTransferSpeed);
            computeShader.SetFloat("deltaTime", Time.deltaTime);
            computeShader.SetFloat("time", Time.time);

            mouseDelta.zw = (int2)(new float2(math.sin(Time.time), math.cos(Time.time)) * 100 + 250);
            computeShader.SetVector("mouseDelta", new Vector4(mouseDelta.x, mouseDelta.y, mouseDelta.z, mouseDelta.w));
            computeShader.SetVector(nameof(gridSize), new Vector4(gridSize.x, gridSize.y, 0, 0));
            Debug.Log(mouseDelta.zw);

            int3 threadGroupSize = new int3(gridSize.x / 8 + 1, gridSize.y / 8 + 1, 1);
            computeShader.Dispatch(applyInkKernel, threadGroupSize.x, threadGroupSize.y, threadGroupSize.z);
            computeShader.Dispatch(calculateInkTransferKernel, threadGroupSize.x, threadGroupSize.y, threadGroupSize.z);
            computeShader.Dispatch(resolveInkKernel, threadGroupSize.x, threadGroupSize.y, threadGroupSize.z);
        }
        else
        {
            new GatherDesiredInkJob()
            {
                inkGrid = inkGrid,
                inkTransferSpeed = inkTransferSpeed,
                gridSize = gridSize,
                inkGridRO = inkGrid, //might explode
                directions = directions,
                deltaTime = Time.deltaTime
            }.Schedule(gridSize.x * gridSize.y, 4).Complete();

            new ResolveInkCellJob()
            {
                inkGrid = inkGrid,
                gridSize = gridSize
            }.Schedule(gridSize.x * gridSize.y, 4).Complete();

            UpdateTexture();
        }
    }

    int2 MouseToIndexPos()
    {
        float3 mousePos = Input.mousePosition;
        int2 snapMousePos = (int2)(mousePos.xy / new float2(Screen.width, Screen.height) * gridSize);
        
        return snapMousePos;
    }

    [BurstCompile]
    public struct GatherDesiredInkJob : IJobParallelFor
    {
        public NativeGrid<InkCell> inkGrid;

        [ReadOnly] public NativeGrid<InkCell> inkGridRO;
        [ReadOnly] public int2x4 directions;

        public float deltaTime;
        public int2 gridSize;
        public float inkTransferSpeed;

        public void Execute(int index)
        {
            int2 inkCellIndex = NativeGrid<InkCell>.IndexToPos(index, gridSize);

            InkCell currentCell = inkGrid[inkCellIndex.x, inkCellIndex.y];

            for (int i = 0; i < 4; i++)
            {
                int2 dir = directions[i];
                int2 nextCellIndex = inkCellIndex + dir;
                if (InBound(nextCellIndex, gridSize))
                {
                    CalculateInkTransfer(inkGridRO, inkCellIndex, nextCellIndex, inkTransferSpeed, deltaTime, out float outcome, out float _);
                    currentCell.incomingInk += outcome;
                }
            }

            inkGrid[inkCellIndex.x, inkCellIndex.y] = currentCell;
        }
    }

    [BurstCompile]
    public struct ResolveInkCellJob : IJobParallelFor
    {
        public NativeGrid<InkCell> inkGrid;
        public int2 gridSize;

        public void Execute(int index)
        {
            int2 inkCellIndex = NativeGrid<InkCell>.IndexToPos(index, gridSize);

            InkCell currentCell = inkGrid[inkCellIndex.x, inkCellIndex.y];
            currentCell.inkLevel += currentCell.incomingInk;
            currentCell.incomingInk = 0;
            inkGrid[inkCellIndex.x, inkCellIndex.y] = currentCell;
        }
    }

    [BurstCompile]
    public static void CalculateInkTransfer(NativeGrid<InkCell> inkGrid, int2 inkCellIndex1, int2 inkCellIndex2, float inkTransferSpeed, float deltaTime, out float inkOutcome1, out float inkOutcome2)
    {
        InkCell inkCell1 = inkGrid[inkCellIndex1.x, inkCellIndex1.y];
        InkCell inkCell2 = inkGrid[inkCellIndex2.x, inkCellIndex2.y];

        float overflowMean = (inkCell1.OverflowAmount + inkCell2.OverflowAmount) / 2f;

        //if a cell can only transfer 1/4 of its ink, we prevent weird ass ink transfer prediction
        float overflowMeanQuarter = overflowMean / 4;

        //outcome 2 can be deducted, validate ink conservation
        float transferAmmount1 = Mathf.MoveTowards(inkCell1.OverflowAmountQuarter, overflowMeanQuarter, deltaTime * inkTransferSpeed);
        float transferAmmount2 = Mathf.MoveTowards(inkCell2.OverflowAmountQuarter, overflowMeanQuarter, deltaTime * inkTransferSpeed);

        inkOutcome1 = transferAmmount1 - inkCell1.OverflowAmountQuarter;
        inkOutcome2 = transferAmmount2 - inkCell2.OverflowAmountQuarter;
        //inkOutcome2 =  inkCell2.OverflowAmountQuarter - transferAmmount1; //
    }

    void UpdateTexture()
    {
        float invMaxInkPerCell = 1f / maxInkPerCell;
        for (int x = 0; x < gridSize.x; x++)
        {
            for (int y = 0; y < gridSize.y; y++)
            {
                float v = 1 - inkGrid[x, y].inkLevel * invMaxInkPerCell;
                textureCpu.SetPixel(x, y, new Color(v, v, v, inkGrid[x, y].InkRatio));
            }
        }
        textureCpu.Apply();
    }
}
