#!/usr/bin/env node
import { readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
const out = resolve(process.env.MEASURE_OUT ?? 'docs/task-switch-performance/evidence');
const replay = JSON.parse(await readFile(resolve(out,'api-replay.json'),'utf8'));
const percentile=(values,p)=>{const v=[...values].sort((a,b)=>a-b);if(!v.length)return null;const i=(v.length-1)*p,a=Math.floor(i);return v[a]+(v[Math.ceil(i)]-v[a])*(i-a);};
const stats=values=>({n:values.length,p50:percentile(values,.5),p95:percentile(values,.95),p99:percentile(values,.99),max:values.length?Math.max(...values):null});
const server=s=>Number(/task-op;dur=([\d.]+)/.exec(s.serverTiming??'')?.[1]);
const summary={percentileMethod:'Linear interpolation at p*(n-1); successful requests only, failures counted separately',
  startedAt:replay.startedAt,finishedAt:replay.finishedAt,groups:{},fields:{}};
for(const kind of [...new Set(replay.samples.map(s=>s.kind))]){
 const all=replay.samples.filter(s=>s.kind===kind), ok=all.filter(s=>s.status===200||s.status===304);
 summary.groups[kind]={attempts:all.length,failures:all.filter(s=>s.error||s.status>=400).length,
   statuses:ok.reduce((a,s)=>(a[s.status]=(a[s.status]??0)+1,a),{}),
   httpMs:stats(ok.map(s=>s.durationMs)),headersMs:stats(ok.map(s=>s.headersMs)),
   handlerMs:stats(ok.map(server).filter(Number.isFinite)),
   downloadMs:stats(ok.map(s=>s.durationMs-s.headersMs)),bytes:stats(ok.map(s=>s.bytes))};
}
const details=replay.samples.filter(s=>s.kind==='detail'&&s.status===200);
for(const field of Object.keys(details[0]?.fields??{}))summary.fields[field]=stats(details.map(s=>s.fields[field]));
summary.cohorts=Object.fromEntries(replay.selected.map(key=>[key,{state:details.find(s=>s.key===key)?.state,
 handlerMs:stats(details.filter(s=>s.key===key).map(server)),httpMs:stats(details.filter(s=>s.key===key).map(s=>s.durationMs))}]));
// Optional offline log input. Aggregate scopes only: without correlation ids,
// git-index-run counts cannot be assigned to an individual browser switch.
if(process.argv[2]){
 const text=await readFile(process.argv[2],'utf8'), groups={};
 for(const line of text.split(/\r?\n/)){
  const m=/task-operation-timing operation=(.*?) method=\S+ path=.*? elapsedMs=([\d.]+)/.exec(line);
  if(m)(groups[m[1]]??=[]).push(Number(m[2]));
 }
 summary.importedLog={scope:'Caller must supply only the desired date window; no silent date filtering',
   timingLines:Object.values(groups).reduce((n,v)=>n+v.length,0),
   operations:Object.fromEntries(Object.entries(groups).map(([k,v])=>[k,stats(v)]))};
}
await writeFile(resolve(out,'summary.json'),JSON.stringify(summary,null,2)+'\n');
console.log(JSON.stringify(summary.groups,null,2));
