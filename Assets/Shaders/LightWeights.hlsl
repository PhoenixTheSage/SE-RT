#include "Common.hlsli"
RWStructuredBuffer<float> OutTree:register(u0);
[numthreads(64,1,1)]void __compute_shader(uint3 tid:SV_DispatchThreadID){uint i=tid.x;
    if(Iteration>0){uint n=Iteration+i;if(i<Iteration)OutTree[n]=OutTree[n*2]+OutTree[n*2+1];return;}
    if(i>=TreeLeaves)return;float power=0;if(i<LightCount){Light l=Lights[i];
        if(l.kind<2)power=luma(l.radiance)*max(l.range*l.range,1);
        else if(l.kind==4)power=luma(l.radiance)*max(l.cone,0);
        else if(l.kind==3)power=luma(Sky.rgb)*4*PI;
        else {Instance inst=Instances[l.instance];Triangle t=Triangles[l.triIndex];Material m=Materials[l.atlasIndex];
            float3 a=mul(float4(Vertices[t.a].p,1),inst.world).xyz,b=mul(float4(Vertices[t.b].p,1),inst.world).xyz,c=mul(float4(Vertices[t.c].p,1),inst.world).xyz;
            float e=0;[unroll]for(uint k=0;k<4;k++){float2 uv=float2((k&1)*.25+.125,(k>>1)*.25+.125);float4 cm,ex;sampleMaterial(t,uv,m,cm,ex);e+=luma(cm.rgb*(1-saturate(cm.a))*m.emission)*ex.x*SunColor.w*.25;}
            power=e*length(cross(b-a,c-a))*.5;}}
    OutTree[TreeLeaves+i]=isfinite(power)?max(0,power):0;
}
