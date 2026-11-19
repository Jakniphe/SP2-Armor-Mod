import math, random
dt=0.01; RATE=10.8*dt
def run(beta, K=8.0, D=0.15, ffgain=1.0, delay=0.0, lag=0.05, dfilt=0.03, ff_tau=0.4, T=14.0, X0=0.5, k_hull=12.9, c_hull=0.4, noise=0.02, want_step=0.0, seed=1, smith_lag=None):
    random.seed(seed)
    gain=K*dt
    psi=X0; u=0.0; A=0.0; r=0.0
    nd=int(round(delay/dt))
    Ahist=[0.0]*(nd+120)
    sl=int(round((smith_lag if smith_lag is not None else lag+delay)/dt)); sl=max(sl,1)
    ffs=0.0; Xprev=None; xd=0.0
    out=[]
    for k in range(int(T/dt)):
        t=k*dt
        want=want_step if t>=1.0 else 0.0
        X=psi+r+random.gauss(0,noise)
        err=want-X
        pend=A-Ahist[-sl]
        th=err-pend
        if Xprev is not None:
            raw=(X-Xprev)/dt; xd+=(raw-xd)*(1-math.exp(-dt/dfilt))
        Xprev=X
        ffs+=(-u-ffs)*(1-math.exp(-dt/ff_tau))
        step=(th-D*xd)*gain
        cmd=A+step+ffgain*ffs*dt
        A+=max(-RATE,min(RATE,cmd-A))
        Ahist.append(A); Ahist.pop(0)
        Ad=Ahist[-1-nd] if nd>0 else A
        rn=r+(Ad-r)*(1-math.exp(-dt/lag)); w=(rn-r)/dt; r=rn
        u+=(beta*w-k_hull*psi-c_hull*u)*dt; psi+=u*dt
        out.append((t,psi,u,r,A,X,want))
    return out
def peak(o,t0=9): return max(abs(x[2]) for x in o if x[0]>=t0)
def freq(o,t0=9):
    xs=[x[2] for x in o if x[0]>=t0]; z=sum(1 for i in range(1,len(xs)) if xs[i]*xs[i-1]<0); return z/2/(14-t0)
if __name__=='__main__':
    print('reproduce: current (K=8,D=.15) vs observed ~62 deg/s, 2.1 Hz  [beta 65, 30]')
    for beta in (30,65):
        for delay in (0,0.02,0.04,0.06,0.08,0.1):
            o=run(beta,delay=delay)
            print(' beta %d delay %3.0f ms: peak %4.0f  freq %.1f Hz'%(beta,delay*1000,peak(o),freq(o)))
