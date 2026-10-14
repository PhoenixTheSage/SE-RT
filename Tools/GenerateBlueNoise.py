"""Original deterministic scalar spatiotemporal void-and-cluster masks.
SPDX-License-Identifier: MIT. Standard library only; no third-party code/data.
Periodic Gaussian energy couples spatial slices and temporal pixel columns.
"""
from pathlib import Path
import math, random
SIDE, FRAMES = 16, 8
points = [(x,y,z) for z in range(FRAMES) for y in range(SIDE) for x in range(SIDE)]
N=len(points)
neighbors=[]
for x,y,z in points:
    row=[]
    for i,(a,b,c) in enumerate(points):
        weight=0
        if c==z:
            dx=min(abs(a-x),SIDE-abs(a-x));dy=min(abs(b-y),SIDE-abs(b-y))
            weight+=math.exp(-(dx*dx+dy*dy)/(2*1.5**2))
        if a==x and b==y:
            dt=min(abs(c-z),FRAMES-abs(c-z))
            weight+=2*math.exp(-dt*dt/(2*1.5**2))
        if weight>1e-5:row.append((i,weight))
    neighbors.append(row)
def change(mask,energy,index,value):
    sign=1 if value else -1
    mask[index]=value
    for i,w in neighbors[index]:energy[i]+=sign*w
channels=[]
for seed in (619,1297,2381,3571):
    rng=random.Random(seed);order=list(range(N));rng.shuffle(order)
    mask=[False]*N;energy=[0.0]*N;priority=[rng.random()*1e-9 for _ in range(N)]
    for i in order[:N//2]:change(mask,energy,i,True)
    # Relax a half-density seed, preserving its exact occupancy.
    for iteration in range(N*8):
        cluster=max((i for i in range(N) if mask[i]),key=lambda i:energy[i]+priority[i])
        change(mask,energy,cluster,False)
        void=min((i for i in range(N) if not mask[i]),key=lambda i:energy[i]+priority[i])
        change(mask,energy,void,True)
        if void==cluster:break
    else:raise RuntimeError('Seed relaxation did not converge')
    optimized=mask.copy();optimized_energy=energy.copy();ranks=[0]*N
    for rank in range(N//2-1,-1,-1):
        cluster=max((i for i in range(N) if mask[i]),key=lambda i:energy[i]+priority[i])
        ranks[cluster]=rank;change(mask,energy,cluster,False)
    mask=optimized;energy=optimized_energy
    for rank in range(N//2,N):
        void=min((i for i in range(N) if not mask[i]),key=lambda i:energy[i]+priority[i])
        ranks[void]=rank;change(mask,energy,void,True)
    channels.append([rank//FRAMES for rank in ranks])
    print('Channel',len(channels)-1,'seed relaxation',iteration+1)
data=bytes(channels[c][i] for i in range(N) for c in range(4))
Path(__file__).resolve().parents[1].joinpath('Assets/Sampling/blue-noise-16x16x8.rgba').write_bytes(data)
print('Generated',len(data),'bytes')
