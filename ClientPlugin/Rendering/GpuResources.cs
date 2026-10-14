using System;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace SERT.Rendering;

internal sealed class GpuBuffer : IDisposable
{
    public Buffer Buffer;
    public ShaderResourceView Srv;
    public UnorderedAccessView Uav;
    public long Bytes;
    public GpuBuffer(Device device, int count, int stride, bool writable = false)
    {
        Bytes = checked((long)Math.Max(count, 1) * stride);
        if (Bytes > int.MaxValue) throw new InvalidOperationException("GPU buffer allocation exceeds D3D11 limit");
        Buffer = new Buffer(device, new BufferDescription((int)Bytes, ResourceUsage.Default,
            BindFlags.ShaderResource | (writable ? BindFlags.UnorderedAccess : BindFlags.None), CpuAccessFlags.None,
            ResourceOptionFlags.BufferStructured, stride));
        Srv = new ShaderResourceView(device, Buffer);
        if (writable) Uav = new UnorderedAccessView(device, Buffer);
    }
    public void Upload<T>(DeviceContext context, T[] data) where T : struct
    {
        if (data.Length == 0) return;
        if ((long)data.Length * Marshal.SizeOf<T>() != Bytes) throw new InvalidOperationException("GPU upload ABI mismatch");
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try { context.UpdateSubresource(new DataBox(pin.AddrOfPinnedObject(), 0, 0), Buffer, 0); }
        finally { pin.Free(); }
    }
    public void Dispose() { Uav?.Dispose(); Srv?.Dispose(); Buffer?.Dispose(); }
}

internal sealed class GpuTexture : IDisposable
{
    public Texture2D Texture;
    public ShaderResourceView Srv;
    public UnorderedAccessView Uav;
    public RenderTargetView Rtv;
    public RenderTargetView[] Slices;
    public long Bytes;
    public GpuTexture(Device device, int width, int height, Format format, int bytesPerPixel, int layers = 1, bool renderTarget = false, bool writable = true, bool array = false)
    {
        Bytes = checked((long)width * height * bytesPerPixel * layers);
        Texture = new Texture2D(device, new Texture2DDescription { Width = width, Height = height, ArraySize = layers,
            MipLevels = 1, Format = format, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | (writable ? BindFlags.UnorderedAccess : 0) | (renderTarget ? BindFlags.RenderTarget : 0) });
        Srv = array ? new ShaderResourceView(device, Texture, new ShaderResourceViewDescription { Format = format,
            Dimension = ShaderResourceViewDimension.Texture2DArray, Texture2DArray = new ShaderResourceViewDescription.Texture2DArrayResource { ArraySize = layers, MipLevels = 1 } }) : new ShaderResourceView(device, Texture);
        if (writable) Uav = new UnorderedAccessView(device, Texture);
        if (renderTarget)
        {
            if (layers == 1 && !array) Rtv = new RenderTargetView(device, Texture);
            else
            {
                Slices = new RenderTargetView[layers];
                for (int i = 0; i < layers; ++i) Slices[i] = new RenderTargetView(device, Texture,
                    new RenderTargetViewDescription { Format = format, Dimension = RenderTargetViewDimension.Texture2DArray,
                        Texture2DArray = new RenderTargetViewDescription.Texture2DArrayResource { ArraySize = 1, FirstArraySlice = i, MipSlice = 0 } });
            }
        }
    }
    public void Dispose() { if (Slices != null) foreach (var slice in Slices) slice.Dispose(); Rtv?.Dispose(); Uav?.Dispose(); Srv?.Dispose(); Texture?.Dispose(); }
}
