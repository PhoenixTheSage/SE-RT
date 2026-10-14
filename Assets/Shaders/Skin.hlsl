#include "Common.hlsli"
struct SkinVertex {float3 p;float u;float3 n;float v;float4 bones,weights;uint pose,p0,p1,p2;};
StructuredBuffer<SkinVertex> Sources:register(t40);
struct Pose {row_major float4x4 transform;};
StructuredBuffer<Pose> Poses:register(t41);
StructuredBuffer<uint> SkinIndices:register(t48);
RWStructuredBuffer<Vertex> OutVertices:register(u0);
RWStructuredBuffer<float> OutErrors:register(u1);
[numthreads(64,1,1)]void __compute_shader(uint3 tid:SV_DispatchThreadID) {
    if(tid.x>=Iteration)return;uint index=SkinIndices[tid.x];SkinVertex s=Sources[index];Vertex v;v.p=s.p;v.n=s.n;v.u=s.u;v.v=s.v;
    float error=0;
    if(dot(s.weights,1)>0){float4 p=0;float3 n=0;[unroll]for(uint b=0;b<4;b++)if(s.weights[b]>0 && s.pose+uint(s.bones[b])<PoseCount){row_major float4x4 m=Poses[s.pose+uint(s.bones[b])].transform;float3 magnitude=mul(abs(s.p),abs((float3x3)m))+abs(m[3].xyz);error+=dot(magnitude,1)*gamma(9)*s.weights[b];p+=mul(float4(s.p,1),m)*s.weights[b];n+=mul(float4(s.n,0),m).xyz*s.weights[b];}v.p=p.xyz;v.n=normalize(n);}
    OutErrors[index]=max(error,1e-6);
    OutVertices[index]=v;
}
