import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, of } from 'rxjs';

import {
  AiProviderModelsResponse,
  AiProviderSettings,
  AiProviderTestResult,
  AiProviderUpdate,
} from '../../core/dtos/ai-provider.dtos';
import { AiProviderService } from '../../core/services/ai-provider.service';
import { AiProviderSettingsComponent } from './ai-provider-settings.component';

const aiProviderSettings: AiProviderSettings = {
  llm: {
    enabled: true,
    baseUrl: 'http://omenhub:20128/v1',
    model: 'qwen3-32b',
    hasKey: true,
    keyFromServerEnv: false,
  },
  stt: {
    enabled: false,
    baseUrl: 'http://omenhub:20128',
    model: 'groq/whisper-large-v3-turbo',
    hasKey: true,
    keyFromServerEnv: true,
  },
  embedding: {
    enabled: false,
    baseUrl: 'https://ai-gateway.vercel.sh/v1',
    model: 'alibaba/qwen3-embedding-0-6b',
    hasKey: false,
    keyFromServerEnv: false,
  },
  alignmentTranscription: {
    enabled: true,
    baseUrl: 'https://transcription.example/v1',
    model: 'alignment-whisper',
    hasKey: true,
    keyFromServerEnv: false,
    useVoiceTranscription: true,
    apiStyle: 'openai-multipart',
  },
};

const aiProviderServiceMock = {
  get: vi.fn<() => Observable<AiProviderSettings>>(),
  update: vi.fn<(update: AiProviderUpdate) => Observable<AiProviderSettings>>(),
  loadModels: vi.fn<() => Observable<AiProviderModelsResponse>>(),
  test: vi.fn<() => Observable<AiProviderTestResult>>(),
};

describe('AiProviderSettingsComponent', () => {
  let fixture: ComponentFixture<AiProviderSettingsComponent>;
  let component: AiProviderSettingsComponent;

  async function configure(settings: AiProviderSettings = aiProviderSettings): Promise<void> {
    TestBed.resetTestingModule();
aiProviderServiceMock.get.mockClear();
    aiProviderServiceMock.get.mockReturnValue(of(settings));
    aiProviderServiceMock.update.mockClear();
    aiProviderServiceMock.update.mockReturnValue(of(settings));
    aiProviderServiceMock.loadModels.mockClear();
    aiProviderServiceMock.loadModels.mockReturnValue(of({ models: ['qwen3-32b', 'gpt-4o'] }));
    aiProviderServiceMock.test.mockClear();
    aiProviderServiceMock.test.mockReturnValue(of({ ok: true, detail: 'Reached the endpoint.' }));

    await TestBed.configureTestingModule({
      imports: [AiProviderSettingsComponent],
      providers: [{ provide: AiProviderService, useValue: aiProviderServiceMock }],
    }).compileComponents();

    fixture = TestBed.createComponent(AiProviderSettingsComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  function input(id: string): HTMLInputElement {
    return fixture.nativeElement.querySelector(`#${id}`) as HTMLInputElement;
  }

  function type(id: string, value: string): void {
    const field = input(id);
    field.value = value;
    field.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  it('loads the effective values itself and never seeds a password field', async () => {
    await configure();

    expect(aiProviderServiceMock.get).toHaveBeenCalledTimes(1);
    expect(input('ai-llm-base-url').value).toBe('http://omenhub:20128/v1');
    expect(input('ai-llm-model').value).toBe('qwen3-32b');
    expect(input('ai-llm-api-key').value).toBe('');
    expect(input('ai-stt-api-key').value).toBe('');
    // The stored key is reported as configured, without rendering it.
    expect(fixture.nativeElement.textContent).toContain('Configured');
  });

  it('sends only the sections that changed and omits an untouched key', async () => {
    await configure();

    type('ai-llm-model', 'gpt-4o');
    component.saveAiProvider();

    expect(aiProviderServiceMock.update).toHaveBeenCalledWith({
      llm: { model: 'gpt-4o' },
    });
  });

  it('sends apiKey: "" only after Clear', async () => {
    await configure();

    component.clearAiKey('llm');
    fixture.detectChanges();
    component.saveAiProvider();

    expect(aiProviderServiceMock.update).toHaveBeenCalledWith({ llm: { apiKey: '' } });
  });

  it('sends a typed key, then leaves it out of the DOM after the save', async () => {
    await configure();

    type('ai-llm-api-key', 'typed-key-value');
    component.saveAiProvider();
    fixture.detectChanges();

    expect(aiProviderServiceMock.update).toHaveBeenCalledWith({ llm: { apiKey: 'typed-key-value' } });
    expect(input('ai-llm-api-key').value).toBe('');
  });

  it('hides the sections the host does not report', async () => {
    await configure({
      llm: aiProviderSettings.llm,
      stt: aiProviderSettings.stt,
    });

    expect(fixture.nativeElement.querySelector('[data-testid="ai-provider-alignment"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="ai-provider-embedding"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="ai-provider-tts"]')).toBeNull();
  });

  it('disables the actions while one request is in flight', async () => {
    await configure();
    aiProviderServiceMock.loadModels.mockReturnValue(new Observable(() => undefined));

    component.loadAiModels('llm');
    fixture.detectChanges();

    expect(component.aiBusy()).toBe(true);
    expect(component.aiLoadingKind()).toBe('llm');
    expect(
      (fixture.nativeElement.querySelector('[data-testid="ai-provider-save"]') as HTMLButtonElement)
        .disabled,
    ).toBe(true);
  });
});
