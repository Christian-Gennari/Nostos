import json,re,hashlib
from pathlib import Path
r=Path(__file__).parent
m=json.loads((r/'manifest.json').read_text())
# Direct EPUB reading of independent Whisper references; no matcher outputs used.
choices={'carol':[(34,'that I know'),(36,'upon\nhis'),(363,'field of strange'),(366,'gleaming berries'),(685,'they are not torn down'),(689,'Marley entered')],
         'pride':[(43,'A single'),(54,'to assure'),(77,'fortnight'),(87,'cannot'),(99,'afterwards'),(103,'ten thousand a year')]}
notes={'carol-03':'Reference writes strain for clipped strange; both durations identify the same paragraph.',
       'carol-05':'Use the second occurrence of they are not torn down, after folding the bed curtains.',
       'pride-02':'10-second reference adds low-probability you after assure; 20-second stops at assure. Range covers adjacent clipped him.',
       'pride-06':'20-second reference adds low-probability yeah and gentleman; range covers year through The gentlemen; yeah is not treated as speech.'}
for c in m['clips']:
    ref=r/'reference'/f"{c['id']}.json"
    x=json.loads(ref.read_text())
    c.update(reviewedBy='Codex: independent local Whisper reference plus direct EPUB text inspection',reviewedAt='2026-10-10',heardExcerpt=x['transcript'],referenceFile=str(ref.relative_to(r)),referenceSha256=hashlib.sha256(ref.read_bytes()).hexdigest(),annotationStatus='confirmed-reference')
    if c['kind']=='introduction':
        c.update(expectedRejection=True,expectedEnd=None,annotationNote='Reference contains recording credits/title/chapter announcement only; no narrative prose. Not human-listened.')
    else:
        order,phrase=choices[c['book']][int(c['positionId'].split('-')[-1])-1]
        doc=json.loads((r/f"{c['book']}.extracted.json").read_text())['document'];block=next(b for b in doc['blocks'] if b['order']==order)
        text=block['text'];matches=list(re.finditer(re.escape(phrase),text,re.I));assert matches,(c['id'],phrase)
        end=matches[-1].end();loc=block['sourceSegments'][0]['locator'];start=loc['startTextOffset']
        lower=max(0,end-12);upper=min(len(text),end+22)
        c.update(expectedRejection=False,expectedEnd={'spineIndex':loc['spineIndex'],'resourceHref':loc['resourceHref'],'minTextOffset':start+lower,'maxTextOffset':start+upper},referenceSourceExcerpt=text[max(0,end-110):min(len(text),end+80)],referenceBlockOrder=order,
                 annotationNote=notes.get(c['positionId'],'Paired references agree on final phrase; bounded range allows punctuation and a clipped adjacent word.')+' Direct source inspection only; not human-listened.')
m.update(annotationMethod='machine-assisted',status='independent-assisted-reference-annotated',referenceMethod={'decoder':'faster-whisper 1.2.1','model':'Systran/faster-whisper-base','localOnly':True,'thresholdsUntouched':True,'humanAudioVerification':False,'approvalIssueComment':'https://github.com/Christian-Gennari/Nostos/issues/747#issuecomment-6098632481'})
(r/'manifest.json').write_text(json.dumps(m,indent=2)+'\n')
print('Annotated',len(m['clips']),'clips')
