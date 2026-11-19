import math, random
dt=0.01; RATE=10.8*dt
def run(beta=65, K=1.1, D=0.15, delay=0.05, lag=0.05, floor=0.0, dead=0.12, quiet_gate=False, T=14.0, X0=1.5, k_hull=12.9, c_hull=0.4, noise=0.01, seed=1, smithL=0.13, efilt=0.0):
    random.seed(seed)
    gain=K*dt
    psi=X0; u=0.0; A=0.0; r=0.0
    nd=int(round(delay/dt)); Ah=[0.0]*(nd+120); sl=max(1,int(round(smithL/dt)))
    xd=0.0; Xp=None; es=0.0
    out=[]
    for k in range(int(T/dt)):
        t=k*dt
        X=psi+r+random.gauss(0,noise)
        err=-X
        pend=A-Ah[-sl]
        th=err-pend
        if Xp is not None:
            raw=(X-Xp)/dt; xd+=(raw-xd)*(1-math.exp(-dt/0.03))
        Xp=X
        if efilt>0:
            es+= (th-es)*(1-math.exp(-dt/efilt)); thf=es
        else: thf=th
        step=(thf-D*xd)*gain
        quiet=(abs(u)<2.0 and abs(xd)<0.3) if quiet_gate else True
        if floor>0 and quiet and abs(th)>dead and math.copysign(1,step)==math.copysign(1,th) and abs(step)<floor:
            step=math.copysign(min(abs(th),floor),th)
        A+=max(-RATE,min(RATE,step))
        Ah.append(A); Ah.pop(0)
        Ad=Ah[-1-nd] if nd>0 else A
        rn=r+(Ad-r)*(1-math.exp(-dt/lag)); w=(rn-r)/dt; r=rn
        u+=(beta*w-k_hull*psi-c_hull*u)*dt; psi+=u*dt
        out.append((t,psi,u,r,A,X))
    return out
def late(o,t0=8): 
    xs=[x[5] for x in o if x[0]>=t0]; us=[x[2] for x in o if x[0]>=t0]
    return max(xs)-min(xs), max(abs(u) for u in us)
if __name__=='__main__':
    print('beta 65 (14 turrets), delay 50 ms: barrel p-p (deg) / hull spin peak (deg/s) after 8 s')
    for K in (0.57,1.1,2.1,8):
        for floor,gate in ((0.0,False),(0.02,False),(0.02,True)):
            o=run(K=K,floor=floor*0.01/0.01*1.0 if False else floor*dt/dt,quiet_gate=gate)
            print(' K=%.2f floor %.2f gate %-5s -> %5.2f / %5.1f'%(K,floor,gate,*late(o)))
