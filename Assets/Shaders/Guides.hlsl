#include <GBuffer/GBuffer.hlsli>
cbuffer GuideParameters:register(b1){uint2 FullSize,Size;};
struct Output { float4 p:SV_Target0; float4 n:SV_Target1; float4 color:SV_Target2; };
Output __pixel_shader(float4 screen:SV_Position) {
    uint2 pixel=min(uint2(screen.xy*FullSize/Size),FullSize-1);SurfaceInterface s=read_gbuffer(pixel);
    Output o;o.p=float4(s.position-get_camera_position(),s.depth);o.n=float4(s.N,1-s.gloss);o.color=float4(s.base_color,s.metalness);
    if(s.native_depth==0 || !all(isfinite(o.p)))o.p=0;return o;
}
