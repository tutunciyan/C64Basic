100 printchr$(147):dim cr(5)
110 poke56334,peek(56334)and254:poke 1,peek(1)and251
120 mg$=" just":gosub 200:mg$="for":gosub 200:mg$=" the":gosub 200
121 mg$="fun":gosub 200:mg$="of it":gosub 200
130 poke 1,peek(1)or4:poke56334,peek(56334)or1
140 goto 110
200 fori=1tolen(mg$):cr(i)=asc(mid$(mg$,i,1))and191:nexti
210 for r = 0 to 7
220 for c = 1 to len(mg$)
230 a=peek(53248+cr(c)*8+r)
240 for n=7 to 0 step-1:b(n)=int(a/2^n):a=a-(b(n)*2^n):next n
250 fort=0 to 7:print mid$(" "+chr$(113),1+b(7-t),1);:next t
260 next c : if len(mg$)<5 then print
270 next r : return
