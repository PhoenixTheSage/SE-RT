Texture2D<float4> CM:register(t0);Texture2D<float4> NG:register(t1);Texture2D<float4> EX:register(t2);Texture2D<float4> AM:register(t3);
SamplerState LinearWrap:register(s0);
cbuffer AtlasParameters:register(b1){uint HasCM,HasNG,HasEX,HasAM;};
struct Output{float4 cm:SV_Target0;float4 ex:SV_Target1;};
Output __pixel_shader(float4 p:SV_Position,float2 uv:TEXCOORD0){Output o;o.cm=HasCM?CM.SampleLevel(LinearWrap,uv,0):float4(1,1,1,0);
    o.ex=float4(HasEX?EX.SampleLevel(LinearWrap,uv,0).y:0,HasNG?1-NG.SampleLevel(LinearWrap,uv,0).a:.5,HasAM?AM.SampleLevel(LinearWrap,uv,0).r:1,HasEX?EX.SampleLevel(LinearWrap,uv,0).r:1);return o;}
