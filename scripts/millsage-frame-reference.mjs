// Development-only reference probe. Runs the pinned upstream Frame subsystem
// against the original archive records; it is not used by the shipped player.
// node scripts/millsage-frame-reference.mjs UPSTREAM_CHECKOUT FIXTURE OUTPUT_JSON [ROW=249]
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { gunzipSync, brotliDecompressSync } from 'node:zlib';
import { createHash } from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

const [checkoutArg, fixtureArg, outputArg, rowArg='249'] = process.argv.slice(2);
if (!checkoutArg || !fixtureArg || !outputArg)
  throw new Error('Expected UPSTREAM_CHECKOUT FIXTURE OUTPUT_JSON [ROW=249]');
const checkout=path.resolve(checkoutArg), fixture=path.resolve(fixtureArg), output=path.resolve(outputArg);
const {stdout}=await promisify(execFile)('git',['-C',checkout,'rev-parse','HEAD'],{encoding:'utf8'});
const commit=stdout.trim();
const expected='a18de5b5e0b3e1fb3fe185008154056f3e7cbce3';
if (commit!==expected) throw new Error(`Reference requires upstream ${expected}; got ${commit}`);
const module = file => import(pathToFileURL(path.join(checkout,file)).href);
const [{screenResources},{PlayerLoop},{EASE},{createStoryUILayers},{frameView,loadFrames},{Frame}]=await Promise.all([
  module('tests/unit/story-screen-resources.mjs'),module('src/engine/loop.js'),module('src/engine/tween.js'),
  module('src/story/interfaces.js'),module('src/story/features/frame.js'),module('src/story/commands/frame.js')]);
const site=path.join(fixture,'sites','intl'), manifestBytes=fs.readFileSync(path.join(site,'stories','10060.json'));
const manifest=JSON.parse(manifestBytes), hash=bytes=>createHash('sha256').update(bytes).digest('hex');
const asset = file => {
  let bytes=fs.readFileSync(path.join(site,file));
  if (file.endsWith('.gz')) bytes=gunzipSync(bytes);
  else if (file.endsWith('.br')) bytes=brotliDecompressSync(bytes);
  return JSON.parse(bytes);
};
const logical = (name,language) => {
  const entry=language ? manifest.languages?.[language]?.files?.[name] || manifest.files[name] : manifest.files[name];
  if (!entry) throw new Error(`Original logical file is absent: ${name}`);
  return entry.parts ? Object.fromEntries(entry.parts.map(([key,file])=>[key,asset(file)])) : asset(entry.asset);
};
const definition=logical('story.json'), original=logical(definition.frames), widget=logical(definition.ui,'zh-Hans');
const episode=logical(definition.episode), row=episode.commands.find(r=>String(r.i)===rowArg);
if (!row || row.cmd!=='Frame') throw new Error(`Row ${rowArg} is not an original Frame command`);
const loop=new PlayerLoop(30), ctx={loop,gl:null,ui:{layers:createStoryUILayers()},episode:{commands:[row]},localize:id=>id};
// Upstream's own unit-test adapter bridges the old serialized record format to
// its current object references. No authored positions/curves/particles change.
const objects=screenResources(ctx,widget,original.frames), controllers=new Map();
const visit=value=>{
  if (!value || typeof value!=='object') return;
  if (value.controller && value.layers) controllers.set(value.controller,value);
  for (const child of Object.values(value)) visit(child);
};
visit(original);
const resolveController=objects.controller;
objects.controller=raw=>resolveController(raw?.layers ? raw : controllers.get(raw?.controller));
const player={ctx,cancelled:false,speedRate:()=>1,calcDuration:(d,def=0)=>d ? Math.max(0,d) : def,
  delay:seconds=>loop.delay(seconds),ease:(name,def=EASE.OutQuad)=>def,noWait:(command,task)=>task};
await loadFrames(ctx);
const view=frameView(ctx),screen=view.screen,canvas=screen.canvas.frame,frame=view.loaded(row.TargetAssetName);
let error;Frame(row,player).catch(failure=>error=failure);
for (let i=0;i<120;i++) {
  await loop.step();screen.layoutAll(960,443);screen.frameParticles?.refresh(loop);
  if(error) throw error;
}
const items=canvas.drawItems((node,alpha)=>screen._extra(node,alpha));
const nodes=[...frame.prefab.nodes.values()].map(node=>({path:node.path,transformOnly:!!node.transformOnly,
  localPosition:node.localPosition,rect:node.rect,scale:[node.localScale.x,node.localScale.y,node.localScaleZ],
  matrix:node.matrix,world:node.matrix3 ? Array.from(node.matrix3) : null}));
const geometry=items.map(item=>({name:item.node?.path||item.node?.name,material:item.material?.id,
  vertices:Array.from(item.verts),indices:Array.from(item.indices||[])}));
const result={upstreamCommit:commit,scope:'Original Frame subsystem only; no GL or full-story pixel comparison',
  sourceManifestSha256:hash(manifestBytes),row,frame:loop.frameCount,viewport:{width:960,height:443},canvas:canvas.size,nodes,geometry};
fs.mkdirSync(path.dirname(output),{recursive:true});fs.writeFileSync(output,JSON.stringify(result,null,2));
if (!fs.readFileSync(path.join(site,'stories','10060.json')).equals(manifestBytes)) throw new Error('Original manifest changed');
console.log(JSON.stringify({output,upstreamCommit:commit,row:row.i,frame:loop.frameCount,canvas:canvas.size,nodes:nodes.length,meshes:geometry.length}));
