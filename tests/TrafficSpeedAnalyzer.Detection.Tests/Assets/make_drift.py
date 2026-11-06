# Versione di known_speed.mp4 con la camera che si sposta molto (come un treppiede urtato), per i test
# della stabilizzazione: vibrazione ±4 px, poi tra 1,5 e 2,5 s uno spostamento di 160 × 90 px con
# rotazione di 0,8°, che resta fino alla fine. Salva lo spostamento di ogni fotogramma rispetto al primo.
# Uso: python make_drift.py known_speed.mp4 drift_known_speed.mp4
import cv2, numpy as np, subprocess, json, sys
src, dst = sys.argv[1], sys.argv[2]
cap = cv2.VideoCapture(src)
W = int(cap.get(3)); H = int(cap.get(4)); FPS = 30
rng = np.random.default_rng(11)
p = subprocess.Popen(['ffmpeg','-v','error','-y','-f','rawvideo','-pix_fmt','bgr24','-s',f'{W}x{H}','-r',str(FPS),'-i','-',
    '-c:v','libx264','-crf','18','-pix_fmt','yuv420p','-metadata','creation_time=2026-10-26T07:00:00.000000Z',dst],stdin=subprocess.PIPE)
motion=[]; n=0
while True:
    ok, fr = cap.read()
    if not ok: break
    t = n/FPS
    s = np.clip((t-1.5)/1.0, 0, 1); s = s*s*(3-2*s)          # spostamento graduale tra 1,5 e 2,5 s
    if n == 0: dx=dy=ang=0.0
    else:
        dx = 160*s + 4*np.sin(2*np.pi*1.3*t+0.4) + rng.normal(0,1.0)
        dy = 90*s + 3*np.sin(2*np.pi*0.9*t+1.1) + rng.normal(0,1.0)
        ang = 0.8*s + 0.3*np.sin(2*np.pi*0.6*t)
    M = cv2.getRotationMatrix2D((W/2,H/2), ang, 1.0); M[0,2]+=dx; M[1,2]+=dy
    out = cv2.warpAffine(fr, M, (W,H), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT)
    p.stdin.write(out.tobytes()); motion.append([round(dx,3),round(dy,3),round(ang,4)]); n+=1
p.stdin.close(); p.wait()
json.dump(dict(description='dx, dy (px) e rotazione (gradi, cv2.getRotationMatrix2D attorno al centro) applicati a ogni fotogramma rispetto al primo',
               width=W, height=H, motion=motion), open(dst.replace('.mp4','.json'),'w'))
print(n, 'fotogrammi', W, H)
