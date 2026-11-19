import math, random
dt=0.01; RATE=10.8*dt
def run(wn=2*math.pi*2.5, zeta=0.17, K=8.0, D=0.15, L=0.02, smith=True, dfilt=0.03, T=12.0, want_step=0.0, X0=2.5, noise=0.0, floor=True, seed=1, gap_dead=0.12):
    random.seed(seed)
    gain=K*dt
    A=0.0; phi=-X0; v=0.0           # barrel yaw error state: phi = barrel angle relative to want (want=0); start 2.5 deg off
    # barrel angle phi follows commanded A through a 2nd order joint: phi'' = wn^2 (A - phi + Aeq) - 2 zeta wn phi'
    # keep A as absolute command; phi absolute barrel angle; want = want_step after t>=1
    A=phi
    Ahist=[A]*100
    sl=max(1,int(round(L/dt)))
    xd=0.0; prev=phi
    out=[]
    for k in range(int(T/dt)):
        t=k*dt
        want=want_step if t>=1.0 else 0.0
        X=phi+random.gauss(0,noise)
        err=want-X
        pend=(A-Ahist[-sl]) if smith else 0.0
        th=err-pend
        raw=(X-prev)/dt; prev=X
        xd+=(raw-xd)*(1-math.exp(-dt/dfilt))
        step=(th-D*xd)*gain
        if floor and abs(th)>gap_dead and math.copysign(1,step)==math.copysign(1,th) and abs(step)<0.02:
            step=math.copysign(min(abs(th),0.02),th)
        A+=max(-RATE,min(RATE,step))
        Ahist.append(A); Ahist.pop(0)
        # joint: semi-implicit
        acc=wn*wn*(A-phi)-2*zeta*wn*v
        v+=acc*dt; phi+=v*dt
        out.append((t,phi,A,want))
    return out
def pp(o,t0=7): 
    xs=[x[1]-x[3] for x in o if x[0]>=t0]; return max(xs)-min(xs)
def freq(o,t0=7):
    xs=[x[1]-x[3] for x in o if x[0]>=t0]; m=sum(xs)/len(xs); z=sum(1 for i in range(1,len(xs)) if (xs[i]-m)*(xs[i-1]-m)<0); return z/2/(12-t0)
if __name__=='__main__':
    print('resonant joint wn=2.5Hz zeta .17 (measured lag ~20 ms), K=8 D=.15 as implemented, smith L=0.02 / 0.14')
    for L in (0.02,0.14):
        o=run(L=L); print('  L=%.2f: barrel p-p %.2f deg, %.1f Hz'%(L,pp(o),freq(o)))
    print('barrel peak-to-peak (deg), 12 s; rows = controller, columns = zeta .1/.17/.3/.6/.9 at wn 2.5 Hz')
    for name,kw in (('current K8 D.15 L.02',dict(K=8,D=0.15,L=0.02)),
                    ('current K8 D.15 L.14',dict(K=8,D=0.15,L=0.14)),
                    ('K8 D.15 no smith',dict(K=8,D=0.15,smith=False)),
                    ('K4 D.15 no smith',dict(K=4,D=0.15,smith=False)),
                    ('K2 D.15 no smith',dict(K=2,D=0.15,smith=False)),
                    ('K8 D.3 no smith',dict(K=8,D=0.3,smith=False)),
                    ('K4 D.3 no smith',dict(K=4,D=0.3,smith=False)),
                    ('K3 D.4 no smith',dict(K=3,D=0.4,smith=False)),
                    ('K2 D.3 no smith',dict(K=2,D=0.3,smith=False))):
        row=[]
        for z in (0.1,0.17,0.3,0.6,0.9):
            o=run(zeta=z,**kw); row.append('%5.2f'%pp(o))
        print('  %-22s %s'%(name,' '.join(row)))
