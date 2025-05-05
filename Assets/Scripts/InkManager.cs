using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UI;

public class InkManager : MonoBehaviour
{
    public struct GpuBrush
    {
        public int2 pos;
        public int2 prevPos;
        public float radius;
    };

    [System.Serializable]
    public struct BrushSettings
    {
        public float radius;
        public Vector2 offset;

        public int chainId;
        public float chainDist;
        public bool canScaleBySpeed;
    };

    public BrushSettings[] brushSettings;

    public RawImage image;

    public Texture2D heightMapRef;

    RenderTexture visualTextureCompute;
    public int2 gridSize;
    public float inkDrop = 5;
    public float maxInkPerCell = 10;
    public float inkTransferSpeed = 2;
    public float drySpeed = 2;
    public float wetnessTransfer = .5f;
    public Color tintColor;
    public float inkDrippingSpeed = 5;
    public float emptySpeed = 5;
    public float emptyInkSaturationMultiplier = 0.5f;

    public float mouseSpeedAvgSmoothing = 6;

    public float minSpeedScaling = 0.2f;
    public float maxSpeedScaling = 5f;
    public float minScaleWithSpeed = 0.5f;
    public float maxScaleWithSpeed = 1.5f;

    int2 prevMousePos;

    RenderTexture inkGridTexture;
    public ComputeShader computeShader;
    private ComputeBuffer buffer;
    private GpuBrush[] brushes;

    float mouseSpeedAvg;

    private Stack<RenderTexture> snapShotStack = new Stack<RenderTexture>();
    private RenderTexture firstSnapShot;

    private unsafe void Start()
    {
        inkGridTexture = CreateInkTexture();

        visualTextureCompute = new RenderTexture(gridSize.x, gridSize.y, 1);
        visualTextureCompute.enableRandomWrite = true;
        visualTextureCompute.filterMode = FilterMode.Trilinear;
        image.texture = visualTextureCompute;

        buffer = new ComputeBuffer(brushSettings.Length, sizeof(GpuBrush), ComputeBufferType.Default);
        brushes = new GpuBrush[brushSettings.Length];

        int initialeKernel = computeShader.FindKernel("InitializeKernel");
        int3 threadGroupSize = new int3(gridSize.x / 8 + 1, gridSize.y / 8 + 1, 1);
        computeShader.SetTexture(initialeKernel, "inkGrid", inkGridTexture);
        computeShader.SetTexture(initialeKernel, "heightMapRef", heightMapRef);
        computeShader.SetFloat(nameof(maxInkPerCell), maxInkPerCell);
        computeShader.SetVector(nameof(gridSize), new Vector4(gridSize.x, gridSize.y, 0, 0));
        computeShader.Dispatch(initialeKernel, threadGroupSize.x, threadGroupSize.y, threadGroupSize.z);

        firstSnapShot = CreateSnapShot();
    }


    RenderTexture CreateInkTexture()
    {
        RenderTexture inkTexture = new RenderTexture(gridSize.x, gridSize.y, 1, UnityEngine.Experimental.Rendering.GraphicsFormat.R32G32B32A32_SFloat);
        inkTexture.enableRandomWrite = true;
        return inkTexture;
    }

    private void OnDestroy()
    {
        buffer.Dispose();
    }

    public static bool InBound(int2 index, int2 gridSize)
    {
        return index.x >= 0 && index.x < gridSize.x && index.y >= 0 && index.y < gridSize.y;
    }

    void Update()
    {
        //lock frame rate because this is a physic sim
        Application.targetFrameRate = 60;

        if (Input.GetMouseButtonDown(0))
        {
            CreateSnapShot();
            mouseSpeedAvg = 0;
            prevMousePos = MouseToIndexPos();

            //update brush
            for (int i = 0; i < brushes.Length; i++)
            {
                brushes[i].pos = prevMousePos;
                brushes[i].prevPos = prevMousePos;
                brushes[i].radius = brushSettings[i].radius;
            }
        }

        if (Input.GetMouseButton(0))
        {
            int2 mousePos = MouseToIndexPos();

            float2 mouseDeltaRatio = (mousePos - prevMousePos) / new float2(Screen.width, Screen.height);
            float mouseSpeed = math.length(mouseDeltaRatio / Time.deltaTime);
            mouseSpeedAvg = math.lerp(mouseSpeedAvg, mouseSpeed, mouseSpeedAvgSmoothing * Time.deltaTime);
            float scaleRatio = math.saturate(math.unlerp(minSpeedScaling, maxSpeedScaling, mouseSpeedAvg));
            float scaleMultiplier = math.lerp(minScaleWithSpeed, maxScaleWithSpeed, scaleRatio);

            for (int i = 0; i < brushes.Length; i++)
            {
                brushes[i].prevPos = brushes[i].pos;

                float2 smoothPos = mousePos;
                int chainId = brushSettings[i].chainId;
                if (chainId != -1 && chainId != i)
                {
                    int2 chainParentPos = brushes[chainId].pos;
                    if (math.distance(brushes[i].pos, chainParentPos) > brushSettings[i].chainDist)
                    {
                        float2 brushToParentDiff = brushes[i].pos - chainParentPos;
                        smoothPos = chainParentPos + math.normalize(brushToParentDiff) * brushSettings[i].chainDist;
                    }
                    else
                    {
                        smoothPos = brushes[i].pos;
                    }
                }

                if (brushSettings[i].canScaleBySpeed)
                {
                    brushes[i].radius = brushSettings[i].radius * scaleMultiplier;
                }

                smoothPos += (float2)brushSettings[i].offset;
                brushes[i].pos = (int2)smoothPos;
            }

            prevMousePos = mousePos;
        }

        if (Input.GetKeyDown(KeyCode.Z))
        {
            LoadSnapShot();
        }

        image.texture = visualTextureCompute;

        int applyInkKernel = computeShader.FindKernel("ApplyInkKernel");
        int emptyInkKernel = computeShader.FindKernel("EmptyInkKernel");
        int calculateInkTransferKernel = computeShader.FindKernel("CalculateInkTransferKernel");
        int calculateInkDrippingKernel = computeShader.FindKernel("CalculateInkDrippingKernel");
        int resolveInkKernel = computeShader.FindKernel("ResolveInkKernel");

        computeShader.SetTexture(applyInkKernel, "inkGrid", inkGridTexture);
        computeShader.SetTexture(emptyInkKernel, "inkGrid", inkGridTexture);
        computeShader.SetTexture(calculateInkTransferKernel, "inkGrid", inkGridTexture);
        computeShader.SetTexture(calculateInkDrippingKernel, "inkGrid", inkGridTexture);
        computeShader.SetTexture(resolveInkKernel, "inkGrid", inkGridTexture);

        computeShader.SetTexture(resolveInkKernel, "visualGrid", visualTextureCompute);

        computeShader.SetFloat(nameof(inkDrop), inkDrop);
        computeShader.SetFloat(nameof(maxInkPerCell), maxInkPerCell);
        computeShader.SetFloat(nameof(inkTransferSpeed), inkTransferSpeed);
        computeShader.SetFloat(nameof(drySpeed), drySpeed);
        computeShader.SetFloat(nameof(wetnessTransfer), wetnessTransfer);
        computeShader.SetFloat(nameof(inkDrippingSpeed), inkDrippingSpeed);
        computeShader.SetFloat("deltaTime", Time.deltaTime);
        computeShader.SetVector(nameof(tintColor), tintColor);
        computeShader.SetVector(nameof(gridSize), new Vector4(gridSize.x, gridSize.y, 0, 0));

        int3 threadGroupSize = new int3(gridSize.x / 8 + 1, gridSize.y / 8 + 1, 1);

        computeShader.SetFloat(nameof(emptySpeed), Input.GetKey(KeyCode.X) ? emptySpeed : 0);
        computeShader.SetFloat(nameof(emptyInkSaturationMultiplier), Input.GetKey(KeyCode.X) ? emptyInkSaturationMultiplier : 1);

        if (Input.GetMouseButton(0))
        {
            buffer.SetData(brushes);
            computeShader.SetBuffer(applyInkKernel, "brushBuffer", buffer);
            computeShader.Dispatch(applyInkKernel, threadGroupSize.x, threadGroupSize.y, threadGroupSize.z);
        }

        computeShader.Dispatch(emptyInkKernel, threadGroupSize.x, threadGroupSize.y, threadGroupSize.z);
        computeShader.Dispatch(calculateInkTransferKernel, threadGroupSize.x, threadGroupSize.y, threadGroupSize.z);
        computeShader.Dispatch(calculateInkDrippingKernel, threadGroupSize.x, threadGroupSize.y, threadGroupSize.z);
        computeShader.Dispatch(resolveInkKernel, threadGroupSize.x, threadGroupSize.y, threadGroupSize.z);
    }

    int2 MouseToIndexPos()
    {
        float3 mousePos = Input.mousePosition;
        int2 snapMousePos = (int2)(mousePos.xy / new float2(Screen.width, Screen.height) * gridSize);

        return snapMousePos;
    }

    RenderTexture CreateSnapShot()
    {
        RenderTexture snapShot = CreateInkTexture();
        Graphics.Blit(inkGridTexture, snapShot);
        snapShotStack.Push(snapShot);
        return snapShot;
    }

    RenderTexture LoadSnapShot()
    {
        RenderTexture snapShot;
        if (snapShotStack.Count > 0)
        {
            snapShot = snapShotStack.Pop(); 
        }
        else
        {
            snapShot = firstSnapShot;
        }
        Graphics.Blit(snapShot, inkGridTexture);
        return snapShot;
    }
}