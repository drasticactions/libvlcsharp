using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using TerraFX.Interop.WinRT;
using Windows.ApplicationModel;
using WinRT;

namespace LibVLCSharp.Platforms.WinUI
{
    /// <summary>
    /// VideoView base class for the WinUI platform
    /// </summary>
    [TemplatePart(Name = PartSwapChainPanelName, Type = typeof(SwapChainPanel))]
    public abstract unsafe class VideoViewBase : Control, IVideoView
    {
        private const string PartSwapChainPanelName = "SwapChainPanel";

        SwapChainPanel? _panel;
        IDXGISwapChain* _swapchain;
        ID3D11RenderTargetView* _swapchainRenderTarget;
        ID3D11Device* _d3dDevice;
        ID3D11DeviceContext* _d3dctx;
        ID3D11Device* _d3deviceVLC;
        ID3D11DeviceContext* _d3dctxVLC;

        ID3D11Texture2D* _textureVLC;
        ID3D11RenderTargetView* _textureRenderTarget;
        HANDLE _sharedHandle;
        ID3D11Texture2D* _texture;
        ID3D11ShaderResourceView* _textureShaderInput;


        ID3D11VertexShader* pVS;
        ID3D11PixelShader* pPS;

        ID3D11InputLayout* pShadersInputLayout;

        ID3D11Buffer* pVertexBuffer;
        int vertexBufferStride;

        uint quadIndexCount;
        ID3D11Buffer* pIndexBuffer;

        ID3D11SamplerState* samplerState;

        const string Mobile = "Windows.Mobile";
        bool _loaded;

        static readonly float BORDER_LEFT = -0.95f;
        static readonly float BORDER_RIGHT = 0.85f;
        static readonly float BORDER_TOP = 0.95f;
        static readonly float BORDER_BOTTOM = -0.90f;

        /// <summary>
        /// The constructor
        /// </summary>
        public VideoViewBase()
        {
            DefaultStyleKey = typeof(VideoViewBase);

            if (!DesignMode.DesignModeEnabled)
            {
                Unloaded += (s, e) => DestroySwapChain();
            }
        }

        ID3D11Buffer* CreateBuffer(D3D11_BUFFER_DESC bd)
        {
            ID3D11Buffer* buffer;

            ThrowIfFailed(_d3dDevice->CreateBuffer(&bd, null, &buffer));

            return buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ShaderInput
        {
            internal Position position;
            internal Texture texture;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Position
        {
            internal float x;
            internal float y;
            internal float z;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Texture
        {
            internal float x;
            internal float y;
        }

        /// <summary>
        /// Invoked whenever application code or internal processes (such as a rebuilding layout pass) call ApplyTemplate. 
        /// In simplest terms, this means the method is called just before a UI element displays in your app.
        /// Override this method to influence the default post-template logic of a class.
        /// </summary>
        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            _panel = (SwapChainPanel)GetTemplateChild(PartSwapChainPanelName);

            if (DesignMode.DesignModeEnabled)
                return;

            DestroySwapChain();

            _panel.SizeChanged += (s, eventArgs) =>
            {
                if (_loaded)
                {
                    UpdateSize();
                }
                else
                {
                    CreateSwapChain();
                }
            };

            _panel.CompositionScaleChanged += (s, eventArgs) =>
            {
                if (_loaded)
                {
                    UpdateScale();
                }
            };

        }

        /// <summary>
        /// Gets the swapchain parameters to pass to the <see cref="LibVLC"/> constructor.
        /// If you don't pass them to the <see cref="LibVLC"/> constructor, the video won't
        /// be displayed in your application.
        /// Calling this property will throw an <see cref="InvalidOperationException"/> if the VideoView is not yet full Loaded.
        /// </summary>
        /// <returns>The list of arguments to be given to the <see cref="LibVLC"/> constructor.</returns>
        public string[] SwapChainOptions
        {
            get
            {
                if (!_loaded)
                {
                    throw new InvalidOperationException("You must wait for the VideoView to be loaded before calling GetSwapChainOptions()");
                }

                return new string[]
                {
                    //$"--winrt-d3dcontext=0x{_d3D11Device!.ImmediateContext.NativePointer.ToString("x")}",
                    //$"--winrt-swapchain=0x{_swapChain!.NativePointer.ToString("x")}"
                };
            }
        }

        /// <summary>
        /// Called when the video view is fully loaded
        /// </summary>
        protected abstract void OnInitialized();

        /// <summary>
        /// Initializes the SwapChain for use with LibVLC
        /// </summary>
        void CreateSwapChain()
        {
            // Do not create the swapchain when the VideoView is collapsed.
            if (_panel == null || _panel.ActualHeight == 0)
                return;

            var objRef = ((IWinRTObject)_panel).NativeObject;

            var desc = new DXGI_SWAP_CHAIN_DESC
            {
                BufferDesc = new DXGI_MODE_DESC
                {
                    Width = (uint)(_panel.ActualWidth * _panel.CompositionScaleX),
                    Height = (uint)(_panel.ActualHeight * _panel.CompositionScaleY),
                    Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
                },
                SampleDesc = new DXGI_SAMPLE_DESC
                {
                    Count = 1
                },
                BufferCount = 1,
                Windowed = TerraFX.Interop.Windows.BOOL.TRUE,
                OutputWindow = (HWND)objRef.ThisPtr,
                BufferUsage = DXGI.DXGI_USAGE_RENDER_TARGET_OUTPUT,
                Flags = (uint)DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH
            };

            uint creationFlags = 0;
#if DEBUG
            creationFlags |= (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_DEBUG;
#endif

            fixed (IDXGISwapChain** swapchain = &_swapchain)
            fixed (ID3D11Device** device = &_d3dDevice)
            fixed (ID3D11DeviceContext** context = &_d3dctx)
            {
                ThrowIfFailed(DirectX.D3D11CreateDeviceAndSwapChain(null,
                        D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                        TerraFX.Interop.Windows.HMODULE.NULL,
                        creationFlags,
                        null,
                        0,
                        D3D11.D3D11_SDK_VERSION,
                        &desc,
                        swapchain,
                        device,
                        null,
                        context));
            }

            ID3D10Multithread* pMultithread;
            var iid = TerraFX.Interop.Windows.IID.IID_ID3D10Multithread;

            ThrowIfFailed(_d3dDevice->QueryInterface(&iid, (void**)&pMultithread));
            pMultithread->SetMultithreadProtected(BOOL.TRUE);
            pMultithread->Release();


            var viewport = new D3D11_VIEWPORT
            {
                Height = (uint)(_panel.ActualHeight * _panel.CompositionScaleY),
                Width = (uint)(_panel.ActualWidth * _panel.CompositionScaleX)
            };

            _d3dctx->RSSetViewports(1, &viewport);

            fixed (ID3D11Device** device = &_d3deviceVLC)
            fixed (ID3D11DeviceContext** context = &_d3dctxVLC)
            {
                ThrowIfFailed(DirectX.D3D11CreateDevice(null,
                      D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                      TerraFX.Interop.Windows.HMODULE.NULL,
                      creationFlags | (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT, /* needed for hardware decoding */
                      null, 0,
                      D3D11.D3D11_SDK_VERSION,
                      device, null, context));
            }

            using ComPtr<ID3D11Resource> pBackBuffer = null;

            iid = TerraFX.Interop.Windows.IID.IID_ID3D11Texture2D;
            ThrowIfFailed(_swapchain->GetBuffer(0, &iid, (void**)pBackBuffer.GetAddressOf()));

            fixed (ID3D11RenderTargetView** swapchainRenderTarget = &_swapchainRenderTarget)
                ThrowIfFailed(_d3dDevice->CreateRenderTargetView(pBackBuffer.Get(), null, swapchainRenderTarget));

            pBackBuffer.Dispose();

            fixed (ID3D11RenderTargetView** swapchainRenderTarget = &_swapchainRenderTarget)
                _d3dctx->OMSetRenderTargets(1, swapchainRenderTarget, null);

            ID3DBlob* VS, PS, pErrBlob;

            using ComPtr<ID3DBlob> vertexShaderBlob = null;

            fixed (byte* shader = Encoding.ASCII.GetBytes(DefaultShaders.HLSL))
            fixed (byte* vshader = Encoding.ASCII.GetBytes("VShader"))
            fixed (byte* vs4 = Encoding.ASCII.GetBytes("vs_4_0"))
            fixed (byte* pshader = Encoding.ASCII.GetBytes("PShader"))
            fixed (byte* ps4 = Encoding.ASCII.GetBytes("ps_4_0"))
            {
                var result = DirectX.D3DCompile(shader, (nuint)DefaultShaders.HLSL.Length, null, null, null, (sbyte*)vshader, (sbyte*)vs4, 0, 0, &VS, &pErrBlob);
                if (result.FAILED && pErrBlob != null)
                {
                    var errorMessage = Encoding.ASCII.GetString((byte*)pErrBlob->GetBufferPointer(), (int)pErrBlob->GetBufferSize());
                    System.Diagnostics.Debug.WriteLine(errorMessage);
                    ThrowIfFailed(result);
                }

                result = DirectX.D3DCompile(shader, (nuint)DefaultShaders.HLSL.Length, null, null, null, (sbyte*)pshader, (sbyte*)ps4, 0, 0, &PS, &pErrBlob);
                if (result.FAILED && pErrBlob != null)
                {
                    var errorMessage = Encoding.ASCII.GetString((byte*)pErrBlob->GetBufferPointer(), (int)pErrBlob->GetBufferSize());
                    System.Diagnostics.Debug.WriteLine(errorMessage);
                    ThrowIfFailed(result);
                }
            }

            fixed (ID3D11VertexShader** vertexShader = &pVS)
            fixed (ID3D11PixelShader** pixelShader = &pPS)
            {
                ThrowIfFailed(_d3dDevice->CreateVertexShader(VS->GetBufferPointer(), VS->GetBufferSize(), null, vertexShader));
                ThrowIfFailed(_d3dDevice->CreatePixelShader(PS->GetBufferPointer(), PS->GetBufferSize(), null, pixelShader));
            }

            fixed (byte* position = Encoding.ASCII.GetBytes("POSITION"))
            fixed (byte* textcoord = Encoding.ASCII.GetBytes("TEXCOORD"))
            fixed (ID3D11InputLayout** shadersInputLayout = &pShadersInputLayout)
            {
                var inputElementDescs = stackalloc D3D11_INPUT_ELEMENT_DESC[2];
                {
                    inputElementDescs[0] = new D3D11_INPUT_ELEMENT_DESC
                    {
                        SemanticName = (sbyte*)position,
                        SemanticIndex = 0,
                        Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT,
                        InputSlot = 0,
                        AlignedByteOffset = D3D11.D3D11_APPEND_ALIGNED_ELEMENT,
                        InputSlotClass = D3D11_INPUT_CLASSIFICATION.D3D11_INPUT_PER_INSTANCE_DATA,
                        InstanceDataStepRate = 0
                    };

                    inputElementDescs[1] = new D3D11_INPUT_ELEMENT_DESC
                    {
                        SemanticName = (sbyte*)textcoord,
                        SemanticIndex = 0,
                        Format = DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT,
                        InputSlot = 0,
                        AlignedByteOffset = D3D11.D3D11_APPEND_ALIGNED_ELEMENT,
                        InputSlotClass = D3D11_INPUT_CLASSIFICATION.D3D11_INPUT_PER_INSTANCE_DATA,
                        InstanceDataStepRate = 0
                    };
                }

                ThrowIfFailed(_d3dDevice->CreateInputLayout(inputElementDescs, 2, VS->GetBufferPointer(), VS->GetBufferSize(), shadersInputLayout));
            }

            var ourVerticles = new ShaderInput[4];

            ourVerticles[0] = new ShaderInput
            {
                position = new Position
                {
                    x = BORDER_LEFT,
                    y = BORDER_BOTTOM,
                    z = 0.0f
                },
                texture = new Texture { x = 0.0f, y = 1.0f }
            };

            ourVerticles[1] = new ShaderInput
            {
                position = new Position
                {
                    x = BORDER_RIGHT,
                    y = BORDER_BOTTOM,
                    z = 0.0f
                },
                texture = new Texture { x = 1.0f, y = 1.0f }
            };

            ourVerticles[2] = new ShaderInput
            {
                position = new Position
                {
                    x = BORDER_RIGHT,
                    y = BORDER_TOP,
                    z = 0.0f
                },
                texture = new Texture { x = 1.0f, y = 0.0f }
            };

            ourVerticles[3] = new ShaderInput
            {
                position = new Position
                {
                    x = BORDER_LEFT,
                    y = BORDER_TOP,
                    z = 0.0f
                },
                texture = new Texture { x = 0.0f, y = 0.0f }
            };

            var verticlesSize = (uint)sizeof(ShaderInput) * 4;

            var bd = new D3D11_BUFFER_DESC
            {
                Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
                ByteWidth = verticlesSize,
                BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_VERTEX_BUFFER,
                CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE
            };

            pVertexBuffer = CreateBuffer(bd);
            vertexBufferStride = Marshal.SizeOf(ourVerticles[0]);

            D3D11_MAPPED_SUBRESOURCE ms;

            ID3D11Resource* res;
            iid = IID.IID_ID3D11Resource;

            ThrowIfFailed(pVertexBuffer->QueryInterface(&iid, (void**)&res));

            ThrowIfFailed(_d3dctx->Map(res, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &ms));
            for (var i = 0; i < ourVerticles.Length; i++)
            {
                Marshal.StructureToPtr(ourVerticles[i], (IntPtr)ms.pData + (i * vertexBufferStride), false);
            }
            //Buffer.MemoryCopy(ms.pData, ourVerticles, verticlesSize, verticlesSize);
            _d3dctx->Unmap(res, 0);

            quadIndexCount = 6;

            var bufferDesc = new D3D11_BUFFER_DESC
            {
                Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
                ByteWidth = sizeof(ushort) * quadIndexCount,
                BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_INDEX_BUFFER,
                CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE
            };

            pIndexBuffer = CreateBuffer(bufferDesc);

            ThrowIfFailed(pIndexBuffer->QueryInterface(&iid, (void**)&res));

            ThrowIfFailed(_d3dctx->Map(res, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &ms));
            Marshal.WriteInt16((IntPtr)ms.pData, 0 * sizeof(ushort), 3);
            Marshal.WriteInt16((IntPtr)ms.pData, 1 * sizeof(ushort), 1);
            Marshal.WriteInt16((IntPtr)ms.pData, 2 * sizeof(ushort), 0);
            Marshal.WriteInt16((IntPtr)ms.pData, 3 * sizeof(ushort), 2);
            Marshal.WriteInt16((IntPtr)ms.pData, 4 * sizeof(ushort), 1);
            Marshal.WriteInt16((IntPtr)ms.pData, 5 * sizeof(ushort), 3);

            _d3dctx->Unmap(res, 0);

            _d3dctx->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D10_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            _d3dctx->IASetInputLayout(pShadersInputLayout);
            uint offset = 0;

            var vv = (uint)vertexBufferStride;
            fixed (ID3D11Buffer** buffer = &pVertexBuffer)
                _d3dctx->IASetVertexBuffers(0, 1, buffer, &vv, &offset);

            _d3dctx->IASetIndexBuffer(pIndexBuffer, DXGI_FORMAT.DXGI_FORMAT_R16_UINT, 0);

            _d3dctx->VSSetShader(pVS, null, 0);
            _d3dctx->PSSetShader(pPS, null, 0);

            var samplerDesc = new D3D11_SAMPLER_DESC
            {
                Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT,
                AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_ALWAYS,
                MinLOD = 0,
                MaxLOD = float.MaxValue,
            };

            fixed (ID3D11SamplerState** ss = &samplerState)
            {
                ThrowIfFailed(_d3dDevice->CreateSamplerState(&samplerDesc, ss));
                _d3dctx->PSSetSamplers(0, 1, ss);
            }

            UpdateScale();
            UpdateSize();
            _loaded = true;
            OnInitialized();
        }

        /// <summary>
        /// Destroys the SwapChain and all related instances.
        /// </summary>
        void DestroySwapChain()
        {
            _loaded = false;
        }

        readonly Guid SWAPCHAIN_WIDTH = new Guid(0xf1b59347, 0x1643, 0x411a, 0xad, 0x6b, 0xc7, 0x80, 0x17, 0x7a, 0x6, 0xb6);
        readonly Guid SWAPCHAIN_HEIGHT = new Guid(0x6ea976a0, 0x9d60, 0x4bb7, 0xa5, 0xa9, 0x7d, 0xd1, 0x18, 0x7f, 0xc9, 0xbd);

        /// <summary>
        /// Associates width/height private data into the SwapChain, so that VLC knows at which size to render its video.
        /// </summary>
        void UpdateSize()
        {
            if (_panel is null)
                return;
        }

        /// <summary>
        /// Updates the MatrixTransform of the SwapChain.
        /// </summary>
        void UpdateScale()
        {
        }

        /// <summary>
        /// When the app is suspended, UWP apps should call Trim so that the DirectX data is cleaned.
        /// </summary>
        void Trim()
        {
        }

        /// <summary>
        /// When the media player is attached to the view.
        /// </summary>
        void Attach()
        {
        }

        /// <summary>
        /// When the media player is detached from the view.
        /// </summary>
        void Detach()
        {
        }


        /// <summary>
        /// Identifies the <see cref="MediaPlayer"/> dependency property.
        /// </summary>
        public static DependencyProperty MediaPlayerProperty { get; } = DependencyProperty.Register(nameof(MediaPlayer), typeof(MediaPlayer),
            typeof(VideoViewBase), new PropertyMetadata(null, OnMediaPlayerChanged));

        /// <summary>
        /// MediaPlayer object connected to the view
        /// </summary>
        public MediaPlayer? MediaPlayer
        {
            get
            {
                return (MediaPlayer?)GetValue(MediaPlayerProperty);
            }
            set
            {
                SetValue(MediaPlayerProperty, value);
                SetMediaPlayerOutput(value);
            }
        }

        /// <summary>
        /// Set Media Player Output.
        /// </summary>
        /// <param name="mediaPlayer">Media Player.</param>
        public void SetMediaPlayerOutput(MediaPlayer? mediaPlayer)
        {
            mediaPlayer?.SetOutputCallbacks(VideoEngine.D3D11, OutputSetup, OutputCleanup, OutputSetResize, UpdateOuput, Swap, StartRendering, null, null, SelectPlane);
        }

        private static void OnMediaPlayerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var videoView = (VideoViewBase)d;
            videoView.Detach();
            if (e.NewValue != null)
            {
                videoView.Attach();
            }
        }

        private static void ThrowIfFailed(HRESULT hr)
        {
            if (hr.FAILED)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }

        bool OutputSetup(ref IntPtr opaque, SetupDeviceConfig* config, ref SetupDeviceInfo setup)
        {
            setup.D3D11.DeviceContext = _d3dctxVLC;
            _d3dctxVLC->AddRef();
            return true;
        }

        void OutputCleanup(IntPtr opaque)
        {
            // here we can release all things Direct3D11 for good (if playing only one file)
            _d3dctxVLC->Release();
        }


        void Swap(IntPtr opaque)
        {
            _swapchain->Present(0, 0);
        }

        void OutputSetResize(IntPtr opaque, MediaPlayer.ReportSizeChange report_size_change, IntPtr report_opaque)
        {

        }

        unsafe bool SelectPlane(IntPtr opaque, UIntPtr plane, void* output)
        {
            if ((ulong)plane != 0)
                return false;
            return true;
        }


        bool UpdateOuput(IntPtr opaque, RenderConfig* config, ref OutputConfig output)
        {
            ReleaseTextures();

            var renderFormat = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;

            var texDesc = new D3D11_TEXTURE2D_DESC
            {
                MipLevels = 1,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE),
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                CPUAccessFlags = 0,
                ArraySize = 1,
                Format = renderFormat,
                Height = config->Height,
                Width = config->Width,
                MiscFlags = (uint)(0x00000002 | 0x00000800)
            };

            fixed (ID3D11Texture2D** texture = &_texture)
                ThrowIfFailed(_d3dDevice->CreateTexture2D(&texDesc, null, texture));

            IDXGIResource1* sharedResource = null;
            var iid = IID.IID_IDXGIResource1;

            _texture->QueryInterface(&iid, (void**)&sharedResource);

            fixed (HANDLE* handle = &_sharedHandle)
                ThrowIfFailed(sharedResource->CreateSharedHandle(null, DXGI.DXGI_SHARED_RESOURCE_READ | DXGI.DXGI_SHARED_RESOURCE_WRITE, null, handle));
            sharedResource->Release();

            ID3D11Device1* d3d11VLC1;
            iid = IID.IID_ID3D11Device1;
            _d3deviceVLC->QueryInterface(&iid, (void**)&d3d11VLC1);

            iid = IID.IID_ID3D11Texture2D;
            fixed (ID3D11Texture2D** texture = &_textureVLC)
                ThrowIfFailed(d3d11VLC1->OpenSharedResource1(_sharedHandle, &iid, (void**)texture));
            d3d11VLC1->Release();

            var shaderResourceViewDesc = new D3D11_SHADER_RESOURCE_VIEW_DESC
            {
                ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_TEXTURE2D,
                Format = texDesc.Format
            };

            shaderResourceViewDesc.Texture2D.MipLevels = 1;

            ID3D11Resource* res;
            iid = IID.IID_ID3D11Resource;
            _texture->QueryInterface(&iid, (void**)&res);
            fixed (ID3D11ShaderResourceView** tsi = &_textureShaderInput)
            {
                ThrowIfFailed(_d3dDevice->CreateShaderResourceView(res, &shaderResourceViewDesc, tsi));
                res->Release();
                _d3dctx->PSSetShaderResources(0, 1, tsi);
            }

            var renderTargetViewDesc = new D3D11_RENDER_TARGET_VIEW_DESC
            {
                Format = texDesc.Format,
                ViewDimension = D3D11_RTV_DIMENSION.D3D11_RTV_DIMENSION_TEXTURE2D
            };

            iid = IID.IID_ID3D11Resource;
            _textureVLC->QueryInterface(&iid, (void**)&res);

            fixed (ID3D11RenderTargetView** trt = &_textureRenderTarget)
            {
                ThrowIfFailed(_d3deviceVLC->CreateRenderTargetView(res, &renderTargetViewDesc, trt));
                res->Release();
                _d3dctxVLC->OMSetRenderTargets(1, trt, null);
            }

            output.Union.DxgiFormat = (int)renderFormat;
            output.FullRange = true;
            output.ColorSpace = ColorSpace.BT709;
            output.ColorPrimaries = ColorPrimaries.BT709;
            output.TransferFunction = TransferFunction.SRGB;

            return true;
        }



        void ReleaseTextures()
        {
            if (_textureVLC != null)
            {
                var count = _textureVLC->Release();
                System.Diagnostics.Debug.Assert(count == 0);
                _textureVLC = null;
            }
            if (_textureShaderInput != null)
            {
                var count = _textureShaderInput->Release();
                System.Diagnostics.Debug.Assert(count == 0);
                _textureShaderInput = null;
            }
            if (_textureRenderTarget != null)
            {
                var count = _textureRenderTarget->Release();
                System.Diagnostics.Debug.Assert(count == 0);
                _textureRenderTarget = null;
            }
            if (_texture != null)
            {
                var count = _texture->Release();
                System.Diagnostics.Debug.Assert(count == 0);
                _texture = null;
            }
        }



        bool StartRendering(IntPtr opaque, bool enter)
        {
            if (enter)
            {
                // DEBUG: draw greenish background to show where libvlc doesn't draw in the texture
                // Normally you should Clear with a black background
                var greenRGBA = new Vector4(0.5f, 0.5f, 0.0f, 1.0f);
                //var blackRGBA = new Vector4(0, 0, 0, 1);

                _d3dctxVLC->ClearRenderTargetView(_textureRenderTarget, (float*)&greenRGBA);
            }
            else
            {
                var orangeRGBA = new Vector4(1.0f, 0.5f, 0.0f, 1.0f);
                _d3dctx->ClearRenderTargetView(_swapchainRenderTarget, (float*)&orangeRGBA);
                // Render into the swapchain
                // We start the drawing of the shared texture in our app as early as possible
                // in hope it's done as soon as Swap_cb is called
                _d3dctx->DrawIndexed(quadIndexCount, 0, 0);
            }
            return true;
        }

    }
}
