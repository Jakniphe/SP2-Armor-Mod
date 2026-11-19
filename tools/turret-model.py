import math, random
dt=0.01; RATE=10.8*dt
def run(beta, gain=0.08, D=0.0, dfilt=0.03, ff_tau=0.4, ffgain=1.0, lag=0.067, T=14.0, want_step=0.0, X0=0.5, k_hull=12.9, c_hull=0.4, noise=0.02, seed=1):
    random.seed(seed)
    psi=X0; u=0.0; A=0.0; r=0.0
    hist=[0.0]*80; L=max(1,int(round(lag/dt)))
    ffs=0.0; Xprev=None; xd=0.0
    out=[]
    for k in range(int(T/dt)):
        t=k*dt
        want=want_step if t>=1.0 else 0.0
        X=psi+r+random.gauss(0,noise)
        err=want-X
        pend=A-hist[-L]
        th=err-pend
        if Xprev is not None:
            raw=(X-Xprev)/dt
            xd+=(raw-xd)*(1-math.exp(-dt/dfilt))
        Xprev=X
        ffs+=(-u-ffs)*(1-math.exp(-dt/ff_tau))
        step=(th - D*xd)*gain
        sm=min(abs(th),0.01)
        if abs(step)<sm and D==0: step=math.copysign(sm,th)
        cmd=A+step+ffgain*ffs*dt
        A+=max(-RATE,min(RATE,cmd-A))
        hist.append(A); hist.pop(0)
        rn=r+(A-r)*(1-math.exp(-dt/lag)); w=(rn-r)/dt; r=rn
        u+=(beta*w-k_hull*psi-c_hull*u)*dt; psi+=u*dt
        out.append((t,psi,u,r,A,X,want))
    return out
def late_peak(o): return max(abs(x[2]) for x in o if x[0]>=9)
def late_err(o):
    xs=[x[6]-x[5] for x in o if x[0]>=9]; return math.sqrt(sum(e*e for e in xs)/len(xs))
def step_metrics(o,target):
    # overshoot and time to within 0.5 deg of target after t=1
    over=max((x[5]-target) for x in o if x[0]>1); ts=None
    for x in o:
        if x[0]>1 and abs(x[5]-target)<0.5:
            ts=x[0]-1; break
    return over,ts
if __name__=='__main__':
    print('stability grid: late hull peak (deg/s) over beta x lag; "ok" when < 5')
    for name,kw in (('current g.08 D0',dict(gain=0.08,D=0.0)),
                    ('g.08 D.1',dict(gain=0.08,D=0.1)),
                    ('g.08 D.2',dict(gain=0.08,D=0.2)),
                    ('g.08 D.4',dict(gain=0.08,D=0.4)),
                    ('g.04 D.2',dict(gain=0.04,D=0.2)),
                    ('g.04 D.4',dict(gain=0.04,D=0.4)),
                    ('g.15 D.3',dict(gain=0.15,D=0.3)),):
        line=[]
        worst=0
        for beta in (-5,15,30,65,100):
            for lag in (0.04,0.067,0.1):
                o=run(beta,lag=lag,**kw); p=late_peak(o); worst=max(worst,p) if beta>0 else worst
                line.append('%4.0f'%p)
        s=run(30,want_step=20.0,T=8.0,**kw); ov,ts=step_metrics(s,20.0)
        print('%-16s'%name,' '.join(line),' | worst(beta>0) %.0f | 20deg step overshoot %.1f settle %s'%(worst,ov,'%.2f'%ts if ts else '-'))
    print('columns: beta -5,15,30,65,100 each with lag .04/.067/.1')
