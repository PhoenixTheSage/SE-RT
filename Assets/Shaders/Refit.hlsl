#include "Common.hlsli"
StructuredBuffer<uint> RefitIndices:register(t42);
RWStructuredBuffer<Node> OutNodes:register(u0);
[numthreads(64,1,1)]void __compute_shader(uint3 tid:SV_DispatchThreadID) {
    if(tid.x>=Iteration)return;uint i=RefitIndices[tid.x];Node n=OutNodes[i];float3 lo=1e30,hi=-1e30;
    if(n.count==0){Node a=OutNodes[n.left],b=OutNodes[n.left+1];lo=min(a.lo,b.lo);hi=max(a.hi,b.hi);}
    else [loop]for(uint k=n.left;k<n.left+n.count;k++) {
        if(i>=TlasRoot){lo=min(lo,Instances[k].lo);hi=max(hi,Instances[k].hi);}
        else {Triangle t=Triangles[k];lo=min(lo,min(Vertices[t.a].p,min(Vertices[t.b].p,Vertices[t.c].p)));hi=max(hi,max(Vertices[t.a].p,max(Vertices[t.b].p,Vertices[t.c].p)));}
    }
    n.lo=lo;n.hi=hi;OutNodes[i]=n;
}
