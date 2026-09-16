import sys, collections
d = open(sys.argv[1], 'rb').read()
c = collections.Counter(); i = 0
names = {1:'P/B slice', 5:'IDR', 6:'SEI', 7:'SPS', 8:'PPS', 9:'AUD'}
while True:
    j = d.find(b'\x00\x00\x01', i)
    if j < 0 or j + 3 >= len(d): break
    c[d[j+3] & 0x1f] += 1; i = j + 3
print(f"{len(d)} bytes; " + ", ".join(f"{names.get(k, k)}={v}" for k, v in sorted(c.items())))
