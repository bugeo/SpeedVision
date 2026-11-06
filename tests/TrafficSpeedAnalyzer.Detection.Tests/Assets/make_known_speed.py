import cv2, numpy as np, subprocess, json
bg=cv2.imread('bg.png').astype(np.float32)
car=cv2.imread('car1.png')
# sfoca la targa (dato personale)
car[195:225,80:130]=cv2.GaussianBlur(car[195:225,80:130],(15,15),0)
car=cv2.resize(car,None,fx=0.6,fy=0.6,interpolation=cv2.INTER_AREA).astype(np.float32)
h,w=car.shape[:2]
alpha=np.zeros((h,w),np.float32); alpha[4:-4,4:-4]=1; alpha=cv2.GaussianBlur(alpha,(9,9),0)
FPS=30; DUR=8.0; MPP=0.1  # 0,1 m/px: 200 px = 20 m
cars=[ # sprite, x centro, verso (+1 giu), v km/h, istante in cui il bordo inferiore e a y0, y0
 dict(img=car,a=alpha,x=250,dirn=-1,kmh=50.0,t0=0.5,y0=470),
 dict(img=car[:,::-1].copy(),a=alpha[:,::-1].copy(),x=540,dirn=-1,kmh=72.0,t0=3.2,y0=470)]
H,W=bg.shape[:2]
p=subprocess.Popen(['ffmpeg','-v','error','-y','-f','rawvideo','-pix_fmt','bgr24','-s',f'{W}x{H}','-r',str(FPS),'-i','-','-c:v','libx264','-crf','18','-pix_fmt','yuv420p','-metadata','creation_time=2026-10-26T07:00:00.000000Z','known_speed.mp4'],stdin=subprocess.PIPE)
truth=[]
for c in cars:
    v=c['kmh']/3.6/MPP*c['dirn']
    # bordo inferiore y(t)=y0+v*(t-t0); attraversamenti di y=380 (B) e y=180 (A)
    tB=c['t0']+(380-c['y0'])/v; tA=c['t0']+(180-c['y0'])/v
    truth.append(dict(kmh=c['kmh'],tA=tA,tB=tB,dirn=c['dirn']))
for n in range(int(DUR*FPS)):
    t=n/FPS; fr=bg.copy()
    for c in cars:
        v=c['kmh']/3.6/MPP*c['dirn']; yb=c['y0']+v*(t-c['t0'])
        tx=c['x']-w/2; ty=yb-h
        if ty>H or ty+h<0: continue
        M=np.float32([[1,0,tx],[0,1,ty]])
        sp=cv2.warpAffine(c['img'],M,(W,H),flags=cv2.INTER_LINEAR); al=cv2.warpAffine(c['a'],M,(W,H),flags=cv2.INTER_LINEAR)[...,None]
        fr=fr*(1-al)+sp*al
    p.stdin.write(np.clip(fr,0,255).astype(np.uint8).tobytes())
p.stdin.close(); p.wait()
json.dump(dict(fps=FPS,meters_per_pixel=MPP,lineA_y=180,lineB_y=380,distance_m=20,reference='bordo inferiore del riquadro',cars=truth),open('known_speed.json','w'),indent=1)
print(json.dumps(truth,indent=1))
