# Versione "mossa" di known_speed.mp4, per i test della stabilizzazione: ogni fotogramma (tranne il primo)
# è traslato e ruotato come in una ripresa su treppiede che vibra. Salva anche lo spostamento applicato.
# Uso: python make_shaky.py known_speed.mp4 shaky_known_speed.mp4
import cv2, numpy as np, subprocess, json, sys
src, dst = sys.argv[1], sys.argv[2]
cap = cv2.VideoCapture(src)
W = int(cap.get(3)); H = int(cap.get(4)); FPS = 30
rng = np.random.default_rng(7)
p = subprocess.Popen(['ffmpeg','-v','error','-y','-f','rawvideo','-pix_fmt','bgr24','-s',f'{W}x{H}','-r',str(FPS),'-i','-',
    '-c:v','libx264','-crf','18','-pix_fmt','yuv420p','-metadata','creation_time=2026-10-26T07:00:00.000000Z',dst],stdin=subprocess.PIPE)
shake=[]; n=0
while True:
    ok, fr = cap.read()
    if not ok: break
    t = n/FPS
    if n == 0: dx=dy=ang=0.0
    else:
        dx = 7*np.sin(2*np.pi*1.3*t+0.4) + rng.normal(0,1.5)
        dy = 5*np.sin(2*np.pi*0.9*t+1.1) + rng.normal(0,1.5)
        ang = 0.5*np.sin(2*np.pi*0.6*t)
    M = cv2.getRotationMatrix2D((W/2,H/2), ang, 1.0); M[0,2]+=dx; M[1,2]+=dy
    out = cv2.warpAffine(fr, M, (W,H), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_REFLECT101)
    p.stdin.write(out.tobytes()); shake.append([round(dx,3),round(dy,3),round(ang,4)]); n+=1
p.stdin.close(); p.wait()
json.dump(dict(description='dx, dy (px) e rotazione (gradi) applicati a ogni fotogramma; il primo è fermo', shake=shake), open(dst.replace('.mp4','.json'),'w'))
print(n, 'fotogrammi')
