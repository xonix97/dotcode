import React from 'react';
import { Code2, Terminal, Cpu, Globe, Zap, Github, Mail, ChevronRight } from 'lucide-react';
import './App.css';

const Nav = () => (
  <nav style={{ 
    padding: '1.5rem 2rem', 
    display: 'flex', 
    justifyContent: 'space-between', 
    alignItems: 'center',
    borderBottom: '1px solid var(--border)',
    position: 'sticky',
    top: 0,
    zIndex: 100,
    backgroundColor: 'var(--background)'
  }}>
    <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem', fontWeight: 'bold', fontSize: '1.5rem', color: 'var(--primary)' }}>
      <Code2 size={28} />
      <span>dotcode</span>
    </div>
    <div style={{ display: 'flex', gap: '2rem', alignItems: 'center' }}>
      <a href="#features" style={{ color: 'var(--text-dim)', fontSize: '0.9rem', textDecoration: 'none' }}>Features</a>
      <a href="#tools" style={{ color: 'var(--text-dim)', fontSize: '0.9rem', textDecoration: 'none' }}>Tools</a>
      <a href="#about" style={{ color: 'var(--text-dim)', fontSize: '0.9rem', textDecoration: 'none' }}>About</a>
      <button className="btn btn-primary" style={{ padding: '0.5rem 1rem', fontSize: '0.8rem' }}>Get Started</button>
    </div>
  </nav>
);

const Hero = () => (
  <section style={{ 
    padding: '6rem 0', 
    textAlign: 'center', 
    backgroundImage: 'radial-gradient(circle at center, #00ff991a 0%, transparent 70%)'
  }}>
    <div className="container">
      <h1 style={{ fontSize: '4rem', marginBottom: '1.5rem', lineHeight: '1.1', fontWeight: '800' }}>
        The AI Agent that <br />
        <span style={{ color: 'var(--primary)' }}>actually writes code.</span>
      </h1>
      <p style={{ fontSize: '1.25rem', color: 'var(--text-dim)', maxWidth: '700px', margin: '0 auto 2.5rem' }}>
        dotcode is a high-performance autonomous coding agent designed to build, maintain, and scale complex software systems with precision and speed.
      </p>
      <div style={{ display: 'flex', gap: '1rem', justifyContent: 'center' }}>
        <button className="btn btn-primary" style={{ fontSize: '1.1rem', display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
          Start Building <ChevronRight size={20} />
        </button>
        <button className="btn btn-outline" style={{ fontSize: '1.1rem' }}>View Documentation</button>
      </div>
    </div>
  </section>
);

const FeatureCard = ({ icon: Icon, title, description }) => (
  <div style={{ 
    padding: '2rem', 
    backgroundColor: 'var(--surface)', 
    borderRadius: '16px', 
    border: '1px solid var(--border)',
    transition: 'border-color 0.3s ease'
  }} 
  onMouseEnter={(e) => e.currentTarget.style.borderColor = 'var(--primary)'}
  onMouseLeave={(e) => e.currentTarget.style.borderColor = 'var(--border)'}>
    <div style={{ color: 'var(--primary)', marginBottom: '1rem' }}><Icon size={32} /></div>
    <h3 style={{ marginBottom: '0.75rem', fontSize: '1.5rem' }}>{title}</h3>
    <p style={{ color: 'var(--text-dim)', lineHeight: '1.6' }}>{description}</p>
  </div>
);

const Features = () => (
  <section id="features" style={{ padding: '6rem 0' }}>
    <div className="container">
      <div style={{ textAlign: 'center', marginBottom: '4rem' }}>
        <h2 style={{ fontSize: '2.5rem', marginBottom: '1rem' }}>Engineered for Autonomy</h2>
        <p style={{ color: 'var(--text-dim)', maxWidth: '600px', margin: '0 auto' }}>
           dotcode doesn't just suggest code; it manages the entire development lifecycle.
        </p>
      </div>
      <div className="grid grid-3">
        <FeatureCard 
          icon={Terminal} 
          title="Shell Execution" 
          description="Full access to the terminal to run tests, install dependencies, and deploy infrastructure." 
        />
        <FeatureCard 
          icon={Code2} 
          title="Multi-file Context" 
          description="Analyzes entire repositories to ensure consistent patterns and zero regressions." 
        />
        <FeatureCard 
          icon={Cpu} 
          title="Self-Correction" 
          description="Iteratively debugs its own code by analyzing compiler errors and runtime logs." 
        />
      </div>
    </div>
  </section>
);

const ToolCard = ({ icon: Icon, name, category }) => (
  <div style={{ 
    padding: '1rem', 
    backgroundColor: 'var(--surface)', 
    borderRadius: '12px', 
    border: '1px solid var(--border)', 
    display: 'flex', 
    alignItems: 'center', 
    gap: '1rem',
    cursor: 'pointer'
  }}>
    <div style={{ color: 'var(--accent)' }}><Icon size={20} /></div>
    <div style={{ flex: 1 }}>
      <div style={{ fontWeight: '600', fontSize: '0.9rem' }}>{name}</div>
      <div style={{ color: 'var(--text-dim)', fontSize: '0.7rem' }}>{category}</div>
    </div>
  </div>
);

const Tools = () => (
  <section id="tools" style={{ padding: '6rem 0', backgroundColor: 'rgba(255,255,255,0.02)' }}>
    <div className="container">
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-end', marginBottom: '4rem' }}>
        <div>
          <h2 style={{ fontSize: '2.5rem', marginBottom: '1rem' }}>The Toolbelt</h2>
          <p style={{ color: 'var(--text-dim)', maxWidth: '500px' }}>
            The capabilities that make dotcode a complete engineer.
          </p>
        </div>
      </div>
      <div className="grid grid-3">
        <ToolCard icon={Globe} name="Network Access" category="Connectivity" />
        <ToolCard icon={Terminal} name="Bash Shell" category="Execution" />
        <ToolCard icon={Code2} name="FileSystem API" category="Storage" />
        <ToolCard icon={Zap} name="LLM Orchestrator" category="Intelligence" />
        <ToolCard icon={Github} name="Git Integration" category="Version Control" />
        <ToolCard icon={Mail} name="API Webhooks" category="Communication" />
      </div>
    </div>
  </section>
);

const Footer = () => (
  <footer style={{ 
    padding: '4rem 0', 
    borderTop: '1px solid var(--border)', 
    backgroundColor: 'var(--background)',
    textAlign: 'center' 
  }}>
    <div className="container">
      <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem', justifyContent: 'center', marginBottom: '2rem', fontWeight: 'bold', fontSize: '1.2rem', color: 'var(--primary)' }}>
        <Code2 size={24} />
        <span>dotcode</span>
      </div>
      <div style={{ display: 'flex', justifyContent: 'center', gap: '2rem', marginBottom: '2rem' }}>
        <a href="#" style={{ color: 'var(--text-dim)', fontSize: '0.9rem', textDecoration: 'none' }}>Twitter</a>
        <a href="#" style={{ color: 'var(--text-dim)', fontSize: '0.9rem', textDecoration: 'none' }}>GitHub</a>
        <a href="#" style={{ color: 'var(--text-dim)', fontSize: '0.9rem', textDecoration: 'none' }}>Discord</a>
      </div>
      <p style={{ color: 'var(--text-dim)', fontSize: '0.8rem' }}>
        © {new Date().getFullYear()} dotcode AI. Built with precision.
      </p>
    </div>
  </footer>
);

function App() {
  return (
    <div style={{ minHeight: '100vh', color: 'var(--text)' }}>
      <Nav />
      <Hero />
      <Features />
      <Tools />
      <Footer />
    </div>
  );
}

export default App;
