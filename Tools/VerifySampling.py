"""Offline mask distribution and low-frequency spectral regression; standard library only."""
from pathlib import Path
import random, math, hashlib
asset = Path(__file__).resolve().parents[1] / 'Assets/Sampling/blue-noise-16x16x8.rgba'
data = asset.read_bytes()
assert len(data) == 8192
modes = [(x,y) for x in range(-2,3) for y in range(-2,3) if 0 < x*x+y*y <= 4]
phase = [[complex(math.cos(2*math.pi*(fx*x+fy*y)/16), math.sin(2*math.pi*(fx*x+fy*y)/16)) for y in range(16) for x in range(16)] for fx,fy in modes]
def spatial(values):
    return sum(abs(sum((values[z*256+i]<128)*p for i,p in enumerate(mode)))**2 for z in range(8) for mode in phase)
def temporal(values):
    return sum(abs(sum((values[z*256+i]<128)*complex(math.cos(2*math.pi*z/8), math.sin(2*math.pi*z/8)) for z in range(8)))**2 for i in range(256))
for channel in range(4):
    values = list(data[channel::4])
    assert all(values.count(rank) == 8 for rank in range(256))
    white = values.copy(); random.Random(541+channel).shuffle(white)
    s, t = spatial(values)/spatial(white), temporal(values)/temporal(white)
    print(f'Channel {channel}: spatial low-frequency ratio {s:.4f}; temporal first-frequency ratio {t:.4f}')
    assert s < .65 and t < .65, 'Low-frequency suppression regression'
print('PASS scalar distributions and spatial/temporal spectra; SHA256',hashlib.sha256(data).hexdigest())
